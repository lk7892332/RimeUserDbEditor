using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MsBoxIcon = MsBox.Avalonia.Enums.Icon;

namespace RimeUserDbEditor;

internal static class Dialogs
{
    public static async Task<bool> Confirm(this Window owner, string message, string? title = null)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title ?? owner.Title ?? string.Empty,
            message, ButtonEnum.YesNo, MsBoxIcon.Question);
        return await box.ShowWindowDialogAsync(owner) == ButtonResult.Yes;
    }

    public static Task ShowInfo(this Window owner, string message, string? title = null)
        => ShowBox(owner, message, title, MsBoxIcon.Info);

    public static Task ShowWarning(this Window owner, string message, string? title = null)
        => ShowBox(owner, message, title, MsBoxIcon.Warning);

    public static Task ShowError(this Window owner, string message, string? title = null)
        => ShowBox(owner, message, title, MsBoxIcon.Error);

    /// <summary>OK/Cancel + info icon —— 給「先彈說明再叫起 picker」這個流程用,
    /// 跟 <see cref="Confirm"/> (YesNo + Question) 的問句語氣不同。</summary>
    public static async Task<bool> ExplainOkCancel(this Window owner, string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(
            title, message, ButtonEnum.OkCancel, MsBoxIcon.Info);
        return await box.ShowWindowDialogAsync(owner) == ButtonResult.Ok;
    }

    private static async Task ShowBox(Window owner, string message, string? title, MsBoxIcon icon)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title ?? owner.Title ?? string.Empty,
            message, ButtonEnum.Ok, icon);
        await box.ShowWindowDialogAsync(owner);
    }
}
