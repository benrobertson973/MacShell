using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacShell.Controls;

namespace MacShell.Apps.Preview;

/// <summary>
/// Dragging a selection, like macOS Preview: press inside a rectangle, oval or lasso selection and the selected pixels
/// lift off and follow the pointer (a see-through hole is left behind; with Option/Alt the original stays — a copy);
/// let go and they are put down there, the selection moving with them (one undo step). Dragged out of the window it
/// becomes a drag-and-drop of a copy: onto another picture (placed inside it), the desktop or Finder (a PNG file), or
/// another app — and this picture is left as it was.
/// </summary>
public partial class PreviewWindow
{
    const string GrabFormat = "MacShell.PreviewGrab";   // where the pointer holds the dragged piece (fractions "x,y")
    Point _testPress;
    bool _testForceCopy;   // (test hook: drag a copy, as with Alt held)

    bool _lifted, _liftCopy;
    PvImage _liftPiece, _liftOriginal;
    BitmapSource _liftBmp;
    Int32Rect _liftR;     // the piece's place in the picture before the drag (image pixels)
    Sel _liftSel;         // the selection before the drag
    int _liftDx, _liftDy;

    /// <summary>Selection tools pick up the selection when pressed inside it.</summary>
    bool CanLift(double ix, double iy) =>
        _tool is Tool.Rect or Tool.Oval or Tool.Lasso && _sel.Kind != SelKind.None && SelCov((int)Math.Floor(ix), (int)Math.Floor(iy)) != 0;

    void BeginLift(double ix, double iy)
    {
        _drag = Drag.Lift;
        _lifted = false;
        _px = ix;
        _py = iy;
        _liftDx = _liftDy = 0;
    }

    /// <summary>Exactly the selection's pixels (outside it: transparent), cut from <paramref name="src"/>.</summary>
    static PvImage SelectionPixels(PvImage src, Sel sel, out Int32Rect r)
    {
        r = PvOps.Intersect(sel.R, src.W, src.H);
        if (r.IsEmpty) return null;
        var o = new PvImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                if (SelCovOf(sel, r.X + x, r.Y + y) != 0) o.Px[y * r.Width + x] = src.Px[(r.Y + y) * src.W + r.X + x];
        return o;
    }

    bool Lift()
    {
        var piece = SelectionPixels(_st.Img, _sel, out var r);
        if (piece == null) { _drag = Drag.None; return false; }
        EndText();
        _lifted = true;
        _liftCopy = _testForceCopy || (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        _liftSel = new Sel { Kind = _sel.Kind, R = _sel.R, Mask = _sel.Mask };
        _liftPiece = piece;
        _liftR = r;
        _liftBmp = (_st.Adj.IsIdentity ? piece : PvOps.AdjustApply(piece, _st.Adj)).ToBitmap();
        _liftOriginal = _st.Img;
        PushUndo();
        if (!_liftCopy)
        {
            // the hole the pixels left
            var holed = _st.Img.Copy();
            for (int y = r.Y; y < r.Y + r.Height; y++)
                for (int x = r.X; x < r.X + r.Width; x++)
                    if (SelCovOf(_liftSel, x, y) != 0) holed.Px[y * holed.W + x] = 0;
            _st.Img = holed;
            _imgGen++;
        }
        return true;
    }

    void LiftMove(double x, double y, double ix, double iy)
    {
        var g = GetView();
        if (!_lifted)
        {
            if (Math.Abs(ix - _px) * g.K < 3 && Math.Abs(iy - _py) * g.K < 3) return;
            if (!Lift()) return;
        }
        if (x < 0 || y < 0 || x >= PxW || y >= PxH) { DragOut(); return; }   // out of the window: drag and drop
        _liftDx = (int)Math.Round(ix - _px);
        _liftDy = (int)Math.Round(iy - _py);
        _sel = new Sel { Kind = _liftSel.Kind, R = new Int32Rect(_liftSel.R.X + _liftDx, _liftSel.R.Y + _liftDy, _liftSel.R.Width, _liftSel.R.Height), Mask = _liftSel.Mask };
        _selGen++;
        Redraw();
    }

    void DrawLift(DrawingContext dc, View g)
    {
        if (!_lifted || _liftBmp == null) return;
        var dg = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(dg, g.K < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
        dg.Children.Add(new ImageDrawing(_liftBmp, new Rect(Math.Round(g.Ox + (_liftR.X + _liftDx) * g.K), Math.Round(g.Oy + (_liftR.Y + _liftDy) * g.K),
            Math.Max(1, Math.Round(_liftR.Width * g.K)), Math.Max(1, Math.Round(_liftR.Height * g.K)))));
        dc.DrawDrawing(dg);
    }

    void LiftDrop()
    {
        if (!_lifted) return;   // (a click inside the selection: nothing changes)
        if (_liftDx == 0 && _liftDy == 0) { CancelLift(); return; }
        var dst = _st.Img.Copy();
        PvOps.OverAt(dst, _liftPiece, _liftR.X + _liftDx, _liftR.Y + _liftDy);
        _st.Img = dst;
        _imgGen++;
        _lifted = false;
        _liftPiece = _liftOriginal = null;
        _liftBmp = null;
        KeepSelectionInside();
        Changed();
    }

    /// <summary>The moved selection, cut to the picture (a mask when part of it went over the edge).</summary>
    void KeepSelectionInside()
    {
        var r = _sel.R;
        int iw = _st.Img.W, ih = _st.Img.H;
        if (r.X >= 0 && r.Y >= 0 && r.X + r.Width <= iw && r.Y + r.Height <= ih) { _selGen++; return; }
        var inside = PvOps.Intersect(r, iw, ih);
        if (inside.IsEmpty) { SelClear(); return; }
        var full = new byte[iw * ih];
        for (int y = inside.Y; y < inside.Y + inside.Height; y++)
            for (int x = inside.X; x < inside.X + inside.Width; x++)
                if (SelCov(x, y) != 0) full[y * iw + x] = 255;
        SetMaskSel(full, iw, ih);
    }

    /// <summary>Puts everything back as it was before the pixels were picked up (Esc, or dragging out of the window).</summary>
    void CancelLift()
    {
        if (_lifted)
        {
            _st.Img = _liftOriginal;
            _imgGen++;
            if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            _sel = _liftSel;
            _selGen++;
        }
        _lifted = false;
        _drag = Drag.None;
        _liftPiece = _liftOriginal = null;
        _liftBmp = null;
        Redraw();
    }

    /// <summary>A copy of the selection as it looks (colour adjustments and markup included), for dragging out.</summary>
    DataObject SelectionDragData(Sel sel, double pressX, double pressY, out PvImage piece, out Point grab)
    {
        piece = SelectionPixels(_st.Flatten(), sel, out var r);
        grab = new Point(0.5, 0.5);
        if (piece == null) return null;
        byte[] png = PvFile.EncodePng(piece);
        string root = Path.Combine(Path.GetTempPath(), "MacShell", "drag");
        try
        {
            // (earlier drags' files: whoever wanted them has copied them by now)
            foreach (var old in Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>())
                if (DateTime.Now - Directory.GetCreationTime(old) > TimeSpan.FromHours(1)) Directory.Delete(old, true);
        }
        catch { }
        string dir = Path.Combine(root, DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, (_untitled ? "Untitled" : Path.GetFileNameWithoutExtension(_name)) + " selection.png");
        File.WriteAllBytes(tmp, png);
        grab = new Point(Math.Clamp((pressX - r.X) / r.Width, 0, 1), Math.Clamp((pressY - r.Y) / r.Height, 0, 1));
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { tmp });   // the desktop, Finder, other apps
        data.SetData("PNG", new MemoryStream(png));
        data.SetData(GrabFormat, $"{grab.X.ToString(CultureInfo.InvariantCulture)},{grab.Y.ToString(CultureInfo.InvariantCulture)}");
        return data;
    }

    void DragOut()
    {
        var sel = _liftSel;
        double px = _px, py = _py;
        CancelLift();   // dragging out takes a copy along: this picture stays as it was
        var data = SelectionDragData(sel, px, py, out var piece, out var grab);
        if (data == null) return;
        var g = GetView();
        double w = piece.W * g.K / Nd.Scale, h = piece.H * g.K / Nd.Scale;
        _canvas.ReleaseMouseCapture();
        DragGhost.RunImage(_canvas, data, DragDropEffects.Copy | DragDropEffects.Move, piece.ToBitmap(), w, h, new Point(grab.X * w, grab.Y * h));
    }

    /// <summary>The grab point of a piece dragged out of another Preview window, if this drag is one.</summary>
    static Point? DroppedGrab(IDataObject data)
    {
        try
        {
            if (data.GetData(GrabFormat) is not string s) return null;
            var p = s.Split(',');
            return new Point(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }
}
