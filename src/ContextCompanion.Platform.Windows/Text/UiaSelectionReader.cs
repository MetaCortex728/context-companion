using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;

namespace ContextCompanion.Platform.Windows.Text;

/// <summary>
/// Reads the focused element's selection through UI Automation's TextPattern (<c>IUIAutomation</c> from
/// UIAutomationCore, called through minimal COM declarations, no WPF). Returns null when the focused element has no
/// TextPattern or no selection.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class UiaSelectionReader : ISelectionReader
{
    /// <summary>Longest text read from one selection, in characters.</summary>
    public const int MaxLength = 1024 * 1024;

    private const int UIA_TextPatternId = 10014;
    private static readonly Guid CUIAutomationClsid = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    public string? ReadSelection()
    {
        object? automationObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CUIAutomationClsid, throwOnError: true)!;
            automationObject = Activator.CreateInstance(type);
            var automation = (IUIAutomation)automationObject!;
            if (automation.GetFocusedElement(out var element) < 0 || element is null) return null;
            if (element.GetCurrentPattern(UIA_TextPatternId, out object? patternObject) < 0 || patternObject is not IUIAutomationTextPattern pattern) return null;
            if (pattern.GetSelection(out var ranges) < 0 || ranges is null) return null;
            if (ranges.get_Length(out int count) < 0 || count == 0) return null;
            var parts = new List<string>(count);
            int remaining = MaxLength;
            for (int i = 0; i < count && remaining > 0; i++)
            {
                if (ranges.GetElement(i, out var range) < 0 || range is null) continue;
                if (range.GetText(remaining, out string? text) >= 0 && !string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                    remaining -= text.Length;
                }
            }
            return parts.Count == 0 ? "" : string.Join(Environment.NewLine, parts);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or UnauthorizedAccessException)
        {
            throw new TextSourceException("UI Automation could not read the selection", ex);
        }
        finally
        {
            if (automationObject is not null && Marshal.IsComObject(automationObject)) Marshal.ReleaseComObject(automationObject);
        }
    }

    // Vtable order follows UIAutomationClient.h. Unused slots are placeholders that are never called.
#pragma warning disable IDE1006, CA1707

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements();
        [PreserveSig] int CompareRuntimeIds();
        [PreserveSig] int GetRootElement();
        [PreserveSig] int ElementFromHandle();
        [PreserveSig] int ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId();
        [PreserveSig] int FindFirst();
        [PreserveSig] int FindAll();
        [PreserveSig] int FindFirstBuildCache();
        [PreserveSig] int FindAllBuildCache();
        [PreserveSig] int BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue();
        [PreserveSig] int GetCurrentPropertyValueEx();
        [PreserveSig] int GetCachedPropertyValue();
        [PreserveSig] int GetCachedPropertyValueEx();
        [PreserveSig] int GetCurrentPatternAs();
        [PreserveSig] int GetCachedPatternAs();
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
    }

    [ComImport, Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        [PreserveSig] int RangeFromPoint();
        [PreserveSig] int RangeFromChild();
        [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray? ranges);
    }

    [ComImport, Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        [PreserveSig] int get_Length(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationTextRange? range);
    }

    [ComImport, Guid("A543CC6A-F4AE-494B-8239-C814481187A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone();
        [PreserveSig] int Compare();
        [PreserveSig] int CompareEndpoints();
        [PreserveSig] int ExpandToEnclosingUnit();
        [PreserveSig] int FindAttribute();
        [PreserveSig] int FindText();
        [PreserveSig] int GetAttributeValue();
        [PreserveSig] int GetBoundingRectangles();
        [PreserveSig] int GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
    }
#pragma warning restore IDE1006, CA1707
}
