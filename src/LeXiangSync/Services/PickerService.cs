using Windows.Storage.Pickers;

namespace LeXiangSync.Services;

/// <summary>文件选择器封装。WinUI 3 下必须把窗口句柄传给 Initialize 才能模态弹出。</summary>
public static class PickerService
{
    public static FileOpenPicker CreateOpenPicker(IntPtr hwnd, params string[] extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        return picker;
    }

    public static FileSavePicker CreateSavePicker(IntPtr hwnd, string suggestedName, params string[] extensions)
    {
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedName;
        if (extensions.Length > 0) picker.FileTypeChoices.Add("数据文件", extensions.ToList());
        return picker;
    }
}
