using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveMark.Core.Options;

namespace DriveMark.App;

/// <summary>
/// 由 <see cref="FioOptionCatalog"/> 自动生成的 fio 参数编辑器。
/// 每个参数一行：中文名 + 类型合适的控件 + 恢复默认按钮；改动即时写回 <see cref="FioJobConfig"/>。
/// </summary>
internal sealed class FioOptionEditor
{
    private readonly FioJobConfig _config;
    private readonly List<Action> _refreshers = [];
    private bool _suppress;

    public FioOptionEditor(FioJobConfig config) => _config = config;

    /// <summary>用户改动参数时触发。</summary>
    public event EventHandler? Changed;

    /// <summary>按分类构建全部参数行。</summary>
    public void BuildInto(Panel host)
    {
        host.Children.Clear();
        _refreshers.Clear();

        foreach (var (category, options) in FioOptionCatalog.GroupedByCategory())
        {
            host.Children.Add(new TextBlock
            {
                Text = category,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                Margin = new Thickness(0, 12, 0, 5),
            });

            foreach (var def in options)
                host.Children.Add(BuildRow(def));
        }
    }

    /// <summary>把配置里的最新值刷回控件（应用预设/加载配置后调用）。</summary>
    public void Refresh()
    {
        foreach (var refresh in _refreshers)
            refresh();
    }

    private FrameworkElement BuildRow(FioOptionDefinition def)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), ToolTip = BuildTooltip(def) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var label = new TextBlock
        {
            Text = def.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = def.Risk switch
            {
                FioRiskLevel.Destructive => Brushes.Firebrick,
                FioRiskLevel.WritesData => Brushes.DarkOrange,
                _ => Brushes.Black,
            },
        };
        if (def.Risk != FioRiskLevel.Safe)
            label.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var (editor, refresh) = CreateEditor(def);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        _refreshers.Add(refresh);

        var reset = new Button
        {
            Content = "↺",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"恢复默认值：{def.DefaultValue ?? "（空）"}",
        };
        reset.Click += (_, _) =>
        {
            _config.Reset(def.Name);
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);

        return row;
    }

    private (FrameworkElement Editor, Action Refresh) CreateEditor(FioOptionDefinition def)
    {
        switch (def.Type)
        {
            case FioOptionType.Bool:
            case FioOptionType.StringSet:
            {
                var checkBox = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = def.Type == FioOptionType.StringSet ? "开关型参数（job 文件中写 1）" : null,
                };
                checkBox.Checked += (_, _) =>
                {
                    if (_suppress)
                        return;
                    _config.TrySet(def.Name, "1");
                    RaiseChanged();
                };
                checkBox.Unchecked += (_, _) =>
                {
                    if (_suppress)
                        return;
                    _config.TrySet(def.Name, "0");
                    RaiseChanged();
                };

                void RefreshCheckBox()
                {
                    _suppress = true;
                    checkBox.IsChecked = _config.EffectiveValue(def.Name) == "1";
                    _suppress = false;
                }

                RefreshCheckBox();
                return (checkBox, RefreshCheckBox);
            }

            case FioOptionType.Enum:
            {
                var comboBox = new ComboBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Height = 24,
                    Padding = new Thickness(4, 0, 0, 0),
                };
                foreach (var choice in def.Choices ?? [])
                    comboBox.Items.Add(choice);
                comboBox.SelectionChanged += (_, _) =>
                {
                    if (_suppress || comboBox.SelectedItem is not string value)
                        return;
                    _config.TrySet(def.Name, value);
                    RaiseChanged();
                };

                void RefreshComboBox()
                {
                    _suppress = true;
                    comboBox.SelectedItem = _config.EffectiveValue(def.Name);
                    _suppress = false;
                }

                RefreshComboBox();
                return (comboBox, RefreshComboBox);
            }

            default:
            {
                var textBox = new TextBox
                {
                    Height = 24,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(4, 0, 4, 0),
                    FontFamily = new FontFamily("Consolas"),
                    ToolTip = $"{def.Help}\n默认值：{def.DefaultValue ?? "（空）"}" +
                              (def.Unit is null ? "" : $"\n单位：{def.Unit}"),
                };
                textBox.TextChanged += (_, _) =>
                {
                    if (_suppress)
                        return;
                    var issue = _config.TrySet(def.Name, textBox.Text);
                    if (issue is null)
                        textBox.ClearValue(Control.BorderBrushProperty);
                    else
                        textBox.BorderBrush = Brushes.Firebrick;
                    RaiseChanged();
                };

                void RefreshTextBox()
                {
                    _suppress = true;
                    textBox.Text = _config.Get(def.Name) ?? string.Empty;
                    textBox.ClearValue(Control.BorderBrushProperty);
                    _suppress = false;
                }

                RefreshTextBox();
                return (textBox, RefreshTextBox);
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string BuildTooltip(FioOptionDefinition def)
    {
        var range = def.Type == FioOptionType.Integer
            ? $"\n范围：{def.Min?.ToString() ?? "-∞"} … {def.Max?.ToString() ?? "+∞"}"
            : string.Empty;
        var choices = def.Choices is { Count: > 0 } ? $"\n可选值：{string.Join(" / ", def.Choices)}" : string.Empty;
        var scope = def.BelongsToJobFile ? (def.BelongsToCommandLine ? "job 文件 + 命令行" : "job 文件") : "仅命令行";
        return $"{def.DisplayName}（{def.Name}）\n\n{def.Help}\n\n默认值：{def.DefaultValue ?? "（空）"}" +
               $"{range}{choices}\n作用域：{scope}\n风险：{def.Risk}\nfio 源码：{def.Source}";
    }
}
