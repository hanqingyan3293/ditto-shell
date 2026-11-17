using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace DittoShell.App.Media;

/// <summary>
/// 缩略图内存 LRU 缓存。不落盘，程序退出即释放。
/// 上限 100 张，按访问顺序淘汰。
/// </summary>
public sealed class ThumbnailCache
{
    private const int Capacity = 100;

    private readonly Dictionary<long, LinkedListNode<(long Id, BitmapSource Bmp)>> _map = new();
    private readonly LinkedList<(long Id, BitmapSource Bmp)> _order = new();
    private readonly object _lock = new();

    public bool TryGet(long id, out BitmapSource? bmp)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bmp = node.Value.Bmp;
                return true;
            }
            bmp = null;
            return false;
        }
    }

    public void Put(long id, BitmapSource bmp)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(id);
            }

            var node = _order.AddFirst((id, bmp));
            _map[id] = node;

            while (_map.Count > Capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Id);
            }
        }
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }
}
