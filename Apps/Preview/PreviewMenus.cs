using System.Windows;
using MacShell.Services;
using MbItem = MacShell.Controls.Mb;

namespace MacShell.Apps.Preview;

/// <summary>Preview's menu-bar menus (NoDitherOS preview.c build_file / build_edit / build_view / build_tools).</summary>
public partial class PreviewWindow
{
    static PreviewWindow FrontDoc => Front;

    public static object[] MenuFile()
    {
        var w = FrontDoc;
        bool ok = w is { _loaded: true };
        return new object[]
        {
            MbItem.Item("New from Clipboard", NewFromClipboard, "⌘N", CanNewFromClipboard),
            MbItem.Item("Open…", OpenFiles, "⌘O"),
            MbItem.Sep(),
            MbItem.Item("Close", () => w?.Close(), "⌘W", w != null),
            MbItem.Item("Save", () => w?.Save(), "⌘S", ok),
            MbItem.Item("Export…", () => w?.BeginExport(), null, ok),
            MbItem.Item("Revert to Saved", () => w?.Revert(), null, w != null && !w._untitled && w.Dirty),
        };
    }

    public static object[] MenuEdit()
    {
        var w = FrontDoc;
        bool ok = w is { _loaded: true };
        bool has = ok && w.HasSomethingSelected;
        return new object[]
        {
            MbItem.Item("Undo", () => w?.Undo(), "⌘Z", ok && w.CanUndo),
            MbItem.Item("Redo", () => w?.Redo(), "⇧⌘Z", ok && w.CanRedo),
            MbItem.Sep(),
            MbItem.Item("Cut", () => w?.Cut(), "⌘X", has),
            MbItem.Item("Copy", () => w?.Copy(), "⌘C", ok),
            MbItem.Item("Paste", () => Paste(w), "⌘V", CanPaste(w)),
            MbItem.Item("Delete", () => w?.DeleteSel(), "⌫", has),
            MbItem.Item("Select All", () => w?.SelectAll(), "⌘A", ok),
            MbItem.Item("Deselect All", () => w?.Deselect(), null, has),
            MbItem.Item("Invert Selection", () => w?.InvertSelection(), "⇧⌘I", ok && w.HasSelection),
        };
    }

    public static object[] MenuView()
    {
        var w = FrontDoc;
        bool ok = w is { _loaded: true };
        return new object[]
        {
            MbItem.Item(w is { _markup: true } ? "Hide Markup Toolbar" : "Show Markup Toolbar", () => w?.ToggleMarkup(), "⇧⌘A", w != null),
            MbItem.Sep(),
            MbItem.Item("Actual Size", () => w?.SetZoom(1), "⌘0", ok),
            MbItem.Item("Zoom to Fit", () => w?.SetZoom(0), "⌘9", ok),
            MbItem.Item("Zoom In", () => w?.ZoomIn(), "⌘+", ok),
            MbItem.Item("Zoom Out", () => w?.ZoomOut(), "⌘-", ok),
        };
    }

    public static object[] MenuTools()
    {
        var w = FrontDoc;
        bool ok = w is { _loaded: true };
        var items = new List<object>
        {
            MbItem.Item("Adjust Color…", PreviewPanels.ShowAdjust, "⇧⌘C", ok),
            MbItem.Item("Adjust Size…", () => w?.BeginSize(), null, ok),
            MbItem.Sep(),
            MbItem.Item("Rotate Left", () => w?.Rotate(false), "⌘L", ok),
            MbItem.Item("Rotate Right", () => w?.Rotate(true), "⌘R", ok),
            MbItem.Item("Flip Horizontal", () => w?.Flip(true), null, ok),
            MbItem.Item("Flip Vertical", () => w?.Flip(false), null, ok),
            MbItem.Sep(),
            MbItem.Item("Crop", () => w?.Crop(), "⌘K", ok && w.HasSelection),
            MbItem.Sep(),
            MbItem.Item("Annotate", null, null, false),
        };
        foreach (var k in new[] { AnKind.Rect, AnKind.Oval, AnKind.Line, AnKind.Arrow, AnKind.Text, AnKind.Bubble, AnKind.Star })
            items.Add(MbItem.Item("    " + Annot.KindName(k), () => w?.InsertShape(k), null, ok));
        items.Add(MbItem.Sep());
        items.Add(MbItem.Item("Show Inspector", PreviewPanels.ShowInspector, "⌘I", ok));
        return items.ToArray();
    }

    /// <summary>Diagnostics ("--open previewdo:&lt;action&gt;"): presses Preview's buttons without a mouse.</summary>
    public void TestAction(string what)
    {
        switch ((what ?? "").ToLowerInvariant())
        {
            case "markup": ToggleMarkup(); break;
            case "selectall": SelectAll(); break;
            case "rotate": Rotate(true); break;
            case "flip": Flip(true); break;
            case "shapes": _markup = true; TogglePopover(Popover.Shapes); break;
            case "style": _markup = true; TogglePopover(Popover.Style); break;
            case "border": _markup = true; TogglePopover(Popover.Border); break;
            case "fill": _markup = true; TogglePopover(Popover.Fill); break;
            case "textstyle": _markup = true; TogglePopover(Popover.Text); break;
            case "nopopover": _popover = Popover.None; Redraw(); break;
            case "arrow": InsertShape(AnKind.Arrow); break;
            case "oval": InsertShape(AnKind.Oval); break;
            case "star": InsertShape(AnKind.Star); break;
            case "bubble": InsertShape(AnKind.Bubble); break;
            case "text": InsertText(); break;
            case "deselect": Deselect(); break;
            case "size": BeginSize(); break;
            case "export": BeginExport(); break;
            case "cancel": SheetCancel(); break;
            case "adjust": PreviewPanels.ShowAdjust(); break;
            case "inspector": PreviewPanels.ShowInspector(); break;
            case "sepia": PushUndo(); _st.Adj.Sepia = 0.6f; _st.Adj.Contrast = 0.2f; Changed(); break;
            case "undo": Undo(); break;
            case "zoomin": ZoomIn(); break;
            case "fit": SetZoom(0); break;
            case "crop": _sel = new Sel { Kind = SelKind.Oval, R = new Int32Rect(100, 100, 400, 300) }; _selGen++; Crop(); break;
            case "savecopy": SaveCopyToDesktopTest(); break;
            case "close": Close(); break;
            case "droptest":
                // (drops the test picture itself onto the middle of the picture area)
                DropPictures(new DataObject(DataFormats.FileDrop, new[] { _path }), new Point(_canvas.ActualWidth * 0.3, _canvas.ActualHeight * 0.6));
                break;
            case "dump":
                File.WriteAllText(Path.Combine(Settings.DataDirectory, "preview.txt"),
                    $"loaded={_loaded} markup={_markup} popover={_popover} sheet={_sheet} annots={_st.An.Count} sel={_sel.Kind} anSel={_anSel} " +
                    $"img={_st.Img?.W}x{_st.Img?.H} k={ViewScale():0.###} px={PxW}x{PxH} scale={Nd.Scale} change={_change} " +
                    string.Join(" | ", _st.An.Select(a => $"{a.Kind} {a.X0:0},{a.Y0:0}-{a.X1:0},{a.Y1:0} w={a.Width:0.#}")));
                break;
        }
    }

    void SaveCopyToDesktopTest()
    {
        // (exports to the scratch folder given by MACSHELL_TEST_EXPORT, never the real Desktop)
        string dir = Environment.GetEnvironmentVariable("MACSHELL_TEST_EXPORT");
        if (string.IsNullOrEmpty(dir)) return;
        _saveIsExport = true;
        Write(Path.Combine(dir, "pvexport.png"), Fmt.Png, 90, unique: false);
    }

    public static void HideAll()
    {
        foreach (var w in All.ToList()) w.Hide();
        PreviewPanels.CloseAll();
    }
}
