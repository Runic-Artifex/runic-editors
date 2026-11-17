using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class VisualStudioAutomation
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    public static IntPtr FindTitle(string title) { return FindWindow(null, title); }
    // PowerShell's COM adapter shows no members on some DTE objects (Window, OutputWindowPane); IDispatch reaches them.
    public static object Get(object target, string name, params object[] arguments)
    { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, arguments); }
    public static object Call(object target, string name, params object[] arguments)
    { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments); }
    // DTE.Windows.Item(vsWindowKindOutput).Object is the OutputWindow (DTE2.ToolWindows is not on the DTE dispatch interface).
    private static object OutputPanes(object dte)
    { return Get(Get(Call(Get(dte, "Windows"), "Item", "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}"), "Object"), "OutputWindowPanes"); }
    public static string PaneText(object dte, string pane)
    {
        object panes = OutputPanes(dte);
        foreach (object candidate in (System.Collections.IEnumerable)panes)
        {
            if ((string)Get(candidate, "Name") != pane) continue;
            object document = Get(candidate, "TextDocument");
            return (string)Call(Call(Get(document, "StartPoint"), "CreateEditPoint"), "GetText", Get(document, "EndPoint"));
        }
        return "";
    }
    public static string PaneNames(object dte)
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (object candidate in (System.Collections.IEnumerable)OutputPanes(dte))
            names.Add((string)Get(candidate, "Name"));
        return string.Join(", ", names);
    }
    public static object FindDte(int processId)
    {
        IRunningObjectTable table; IBindCtx context;
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
        IEnumMoniker enumerator; table.EnumRunning(out enumerator);
        try
        {
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                string name; monikers[0].GetDisplayName(context, null, out name);
                if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(":" + processId, StringComparison.Ordinal))
                { object value; table.GetObject(monikers[0], out value); return value; }
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(enumerator); Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(table); }
    }
}
