namespace DittoShell.App.ViewModels;

/// <summary>斜杠命令面板里的一条命令。</summary>
public sealed class SlashCommandVM
{
    public SlashCommandVM(string name, string label, string desc, Action run)
    {
        Name = name;
        Label = label;
        Desc = desc;
        Run = run;
    }

    public string Name { get; }
    public string Label { get; }
    public string Desc { get; }
    public Action Run { get; }

    public string Display => $"{Name}  ·  {Label}";
}
