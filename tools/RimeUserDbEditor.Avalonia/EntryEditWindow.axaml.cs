using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RimeUserDbEditor;

public sealed partial class EntryEditWindow : Window
{
    public UserDbEntry Entry { get; private set; } = new();

    public EntryEditWindow() { InitializeComponent(); }

    public EntryEditWindow(UserDbEntry initial) : this()
    {
        TextBox_.Text   = initial.Text;
        CodeBox.Text    = initial.Code;
        CommitsBox.Text = initial.Commits.ToString(CultureInfo.InvariantCulture);
        DeeBox.Text     = initial.Dee.ToString("G", CultureInfo.InvariantCulture);
        TickBox.Text    = initial.Tick.ToString(CultureInfo.InvariantCulture);
    }

    private async void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        // text/code 要當 string 存進 Entry,先 ?? 拿掉 nullable;
        // 數值欄位的 TryParse 接受 null,沒必要先收成 local。
        string text = TextBox_.Text ?? string.Empty;
        string code = CodeBox.Text  ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(code))
        {
            await this.ShowWarning("詞語與編碼不能留空。");
            return;
        }
        if (!int.TryParse(CommitsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int commits))
        {
            await this.ShowWarning("Commits (c) 必須是整數（負值代表刪除）。");
            return;
        }
        if (!double.TryParse(DeeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dee))
        {
            await this.ShowWarning("Dee (d) 必須是有效的數值。");
            return;
        }
        if (!ulong.TryParse(TickBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong tick))
        {
            await this.ShowWarning("Tick (t) 必須是非負整數。");
            return;
        }

        Entry = new UserDbEntry
        {
            Text    = text,
            Code    = code,
            Commits = commits,
            Dee     = dee,
            Tick    = tick,
        };
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
