using System;
using System.Runtime.InteropServices;

static class FolderPicker {
    [ComImport,Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")] class Dialog {}
    [ComImport,Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint n,IntPtr specs); void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint i);
        void Advise(IntPtr e,out uint cookie);void Unadvise(uint cookie);
        void SetOptions(uint flags); void GetOptions(out uint flags);
        void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem item);void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string value);void GetFileName(out IntPtr s);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string value);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string value);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string value);void GetResult(out IShellItem item);
    }
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem {
        void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out IntPtr result);
        void GetParent(out IShellItem parent);void GetDisplayName(uint kind,[MarshalAs(UnmanagedType.LPWStr)]out string path);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
    static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out IShellItem item);
    public static string Choose(IntPtr owner,string current){
        var d=(IFileDialog)new Dialog();IShellItem initial=null,result=null;
        try{uint flags;d.GetOptions(out flags);d.SetOptions(flags|0x20|0x40|0x800);d.SetTitle("Куда сохранить полученный файл?");d.SetOkButtonLabel("Выбрать папку");
            if(System.IO.Directory.Exists(current)){var iid=typeof(IShellItem).GUID;SHCreateItemFromParsingName(current,IntPtr.Zero,ref iid,out initial);d.SetFolder(initial);}
            int hr=d.Show(owner);if(hr==unchecked((int)0x800704C7))return null;Marshal.ThrowExceptionForHR(hr);
            d.GetResult(out result);string path;result.GetDisplayName(0x80058000,out path);return path;
        }finally{if(result!=null)Marshal.ReleaseComObject(result);if(initial!=null)Marshal.ReleaseComObject(initial);Marshal.ReleaseComObject(d);}
    }
}
