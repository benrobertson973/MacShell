using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MacShell.Controls;

/// <summary>
/// SF Symbols–style line glyphs drawn on a 24×24 grid. Parts prefixed with "!" are filled,
/// "circle:cx,cy,r" and "rect:x,y,w,h,r" are shorthand primitives.
/// </summary>
public static class Sym
{
    static readonly Dictionary<string, string[]> Defs = new()
    {
        ["clock"] = new[] { "circle:12,12,9", "M12,7 V12 L15.5,14.2" },
        // weather
        ["sun.max"] = new[] { "circle:12,12,4.3", "M12,2.6 V4.9 M12,19.1 V21.4 M2.6,12 H4.9 M19.1,12 H21.4 M5.35,5.35 L7,7 M17,17 L18.65,18.65 M5.35,18.65 L7,17 M17,7 L18.65,5.35" },
        ["cloud"] = new[] { "M7.1,18.5 H17.4 A3.9,3.9 0 0 0 17.8,10.7 A5.8,5.8 0 0 0 6.9,11.7 A3.4,3.4 0 0 0 7.1,18.5 Z" },
        ["cloud.sun"] = new[] { "circle:8.2,8.4,2.9", "M8.2,2.6 V3.7 M2.4,8.4 H3.5 M4.1,4.3 L4.9,5.1 M12.3,4.3 L11.5,5.1 M4.1,12.5 L4.9,11.7",
                                "M10.2,20.2 H18.2 A3.3,3.3 0 0 0 18.5,13.6 A4.9,4.9 0 0 0 9.4,14.4 A2.9,2.9 0 0 0 10.2,20.2 Z" },
        ["cloud.moon"] = new[] { "M12.6,8.7 A5.2,5.2 0 1 1 6.1,2.6 A4.2,4.2 0 0 0 12.6,8.7 Z",
                                 "M10.2,20.2 H18.2 A3.3,3.3 0 0 0 18.5,13.6 A4.9,4.9 0 0 0 9.4,14.4 A2.9,2.9 0 0 0 10.2,20.2 Z" },
        ["cloud.rain"] = new[] { "M7.1,14.2 H17.4 A3.9,3.9 0 0 0 17.8,6.4 A5.8,5.8 0 0 0 6.9,7.4 A3.4,3.4 0 0 0 7.1,14.2 Z", "M8.6,16.8 L7.6,19.6 M12.6,16.8 L11.6,19.6 M16.6,16.8 L15.6,19.6" },
        ["cloud.heavyrain"] = new[] { "M7.1,14.2 H17.4 A3.9,3.9 0 0 0 17.8,6.4 A5.8,5.8 0 0 0 6.9,7.4 A3.4,3.4 0 0 0 7.1,14.2 Z", "M7.6,16.6 L6.2,20.6 M11.1,16.6 L9.7,20.6 M14.6,16.6 L13.2,20.6 M18.1,16.6 L16.7,20.6" },
        ["cloud.drizzle"] = new[] { "M7.1,14.2 H17.4 A3.9,3.9 0 0 0 17.8,6.4 A5.8,5.8 0 0 0 6.9,7.4 A3.4,3.4 0 0 0 7.1,14.2 Z", "!circle:8.4,17.6,0.95", "!circle:12.4,17.6,0.95", "!circle:16.4,17.6,0.95", "!circle:10.4,20.4,0.95", "!circle:14.4,20.4,0.95" },
        ["cloud.snow"] = new[] { "M7.1,14.2 H17.4 A3.9,3.9 0 0 0 17.8,6.4 A5.8,5.8 0 0 0 6.9,7.4 A3.4,3.4 0 0 0 7.1,14.2 Z", "M8.4,16.6 V19.4 M7,18 H9.8", "M15.6,16.6 V19.4 M14.2,18 H17", "M12,18.9 V21.7 M10.6,20.3 H13.4" },
        ["cloud.bolt.rain"] = new[] { "M7.1,14.2 H17.4 A3.9,3.9 0 0 0 17.8,6.4 A5.8,5.8 0 0 0 6.9,7.4 A3.4,3.4 0 0 0 7.1,14.2 Z", "!M12.9,14.6 L10.4,18.6 H12.6 L11.2,22 L14.8,17.3 H12.6 L13.9,14.6 Z", "M7.8,16.8 L7,19 M17.4,16.8 L16.6,19" },
        ["location.fill"] = new[] { "!M20.6,3.4 L3.5,10.8 L11.2,12.8 L13.2,20.5 Z" },
        ["cloud.fog"] = new[] { "M7.1,13.6 H17.4 A3.9,3.9 0 0 0 17.8,5.8 A5.8,5.8 0 0 0 6.9,6.8 A3.4,3.4 0 0 0 7.1,13.6 Z", "M4.5,16.8 H19.5", "M6.5,19.8 H17.5" },
        // Mail
        ["envelope"] = new[] { "rect:3,5.5,18,13,2.2", "M3.7,6.8 L12,13 L20.3,6.8" },
        ["tray"] = new[] { "M3.5,13 L6,5.6 A1.5,1.5 0 0 1 7.4,4.6 H16.6 A1.5,1.5 0 0 1 18,5.6 L20.5,13 V18 A2,2 0 0 1 18.5,20 H5.5 A2,2 0 0 1 3.5,18 Z", "M3.5,13 H8.4 L9.6,15.4 H14.4 L15.6,13 H20.5" },
        ["paperplane"] = new[] { "M20.8,3.2 L3.4,10.3 L10.3,13.7 L13.7,20.6 Z", "M20.8,3.2 L10.3,13.7" },
        ["paperclip"] = new[] { "M16.6,7.2 L9.3,14.5 A1.8,1.8 0 0 0 11.8,17 L18.7,10.1 A3.6,3.6 0 0 0 13.6,5 L6.4,12.2 A5.4,5.4 0 0 0 14,19.8 L19.6,14.2" },
        ["archivebox"] = new[] { "rect:3,4,18,4.6,1.2", "M4.6,8.6 V18 A2,2 0 0 0 6.6,20 H17.4 A2,2 0 0 0 19.4,18 V8.6", "M9.5,12.2 H14.5" },
        ["xmark.bin"] = new[] { "rect:3.5,4,17,4.5,1.2", "M5,8.5 V18 A2,2 0 0 0 7,20 H17 A2,2 0 0 0 19,18 V8.5", "M9.8,11.6 L14.2,16 M14.2,11.6 L9.8,16" },
        ["flag"] = new[] { "M5.5,21 V4", "M5.5,4.6 C8.5,2.9 11,6.1 14,4.7 C15.8,3.9 17.2,4 18.5,4.5 V13.3 C17.2,12.8 15.8,12.7 14,13.5 C11,14.9 8.5,11.7 5.5,13.4" },
        ["flag.fill"] = new[] { "M5.5,21 V4", "!M5.5,4.6 C8.5,2.9 11,6.1 14,4.7 C15.8,3.9 17.2,4 18.5,4.5 V13.3 C17.2,12.8 15.8,12.7 14,13.5 C11,14.9 8.5,11.7 5.5,13.4 Z" },
        ["arrowshape.turn.up.left"] = new[] { "M9.5,5.5 L4,11 L9.5,16.5", "M4.6,11 H13.5 A6,6 0 0 1 19.5,17 V19.5" },
        ["arrowshape.turn.up.left.2"] = new[] { "M11.5,5.5 L6,11 L11.5,16.5", "M7.5,5.5 L2,11 L7.5,16.5", "M6.6,11 H14.5 A6,6 0 0 1 20.5,17 V19.5" },
        ["arrowshape.turn.up.right"] = new[] { "M14.5,5.5 L20,11 L14.5,16.5", "M19.4,11 H10.5 A6,6 0 0 0 4.5,17 V19.5" },
        ["folder"] = new[] { "M3,7 A2,2 0 0 1 5,5 H9.4 L11.4,7.2 H19 A2,2 0 0 1 21,9.2 V17.8 A2,2 0 0 1 19,19.8 H5 A2,2 0 0 1 3,17.8 Z", "M3,10.2 H21" },
        ["line.3.horizontal.decrease.circle"] = new[] { "circle:12,12,9", "M7.5,9 H16.5 M9.2,12 H14.8 M10.9,15 H13.1" },
        ["apps"] = new[] { "M6.5,19.5 L12,4.5 L17.5,19.5", "M8.6,14 H15.4", "M4.5,19.5 H8.5", "M15.5,19.5 H19.5" },
        ["desktop"] = new[] { "rect:2.5,4.5,19,15,2.5", "M2.5,8.2 H21.5", "!rect:7.2,15.3,9.6,1.9,0.95" },
        ["doc"] = new[] { "M6.5,3 H13.2 L18.5,8.3 V19 A2,2 0 0 1 16.5,21 H6.5 A2,2 0 0 1 4.5,19 V5 A2,2 0 0 1 6.5,3 Z", "M13.2,3 V6.8 A1.5,1.5 0 0 0 14.7,8.3 H18.5" },
        ["doc.text"] = new[] { "M6.5,3 H13.2 L18.5,8.3 V19 A2,2 0 0 1 16.5,21 H6.5 A2,2 0 0 1 4.5,19 V5 A2,2 0 0 1 6.5,3 Z", "M13.2,3 V6.8 A1.5,1.5 0 0 0 14.7,8.3 H18.5", "M8,12.5 H15 M8,15.5 H15 M8,18 H12" },
        ["arrow.down.circle"] = new[] { "circle:12,12,9", "M12,7 V16.5", "M8,12.8 L12,16.8 L16,12.8" },
        ["house"] = new[] { "M2.8,11.6 L12,3.6 L21.2,11.6", "M5.5,9.5 V19 A1,1 0 0 0 6.5,20 H9.8 V14.8 A0.8,0.8 0 0 1 10.6,14 H13.4 A0.8,0.8 0 0 1 14.2,14.8 V20 H17.5 A1,1 0 0 0 18.5,19 V9.5" },
        ["photo"] = new[] { "rect:2.8,4.8,18.4,14.4,2.6", "M3.3,17.3 L8.3,12.5 L12.6,16.6 L15.2,14.1 L20.7,19", "!circle:15.6,9.4,1.7" },
        ["music.note"] = new[] { "M9.2,17.6 V5.8 L19.2,3.6 V15.6", "M9.2,9 L19.2,6.8", "!circle:6.9,17.8,2.4", "!circle:16.9,15.8,2.4" },
        ["film"] = new[] { "rect:3,3.8,18,16.4,2.5", "M7.6,3.8 V20.2", "M16.4,3.8 V20.2", "M3,9.3 H7.6 M3,14.7 H7.6 M16.4,9.3 H21 M16.4,14.7 H21", "M7.6,12 H16.4" },
        ["icloud"] = new[] { "M7.2,18.5 H16.9 A4.1,4.1 0 0 0 17.5,10.3 A5.8,5.8 0 0 0 6.5,9.4 A4.6,4.6 0 0 0 7.2,18.5 Z" },
        ["internaldrive"] = new[] { "rect:2.5,6.8,19,10.4,2.6", "M5.6,12 H11", "!circle:17.6,12,1.15" },
        ["externaldrive"] = new[] { "rect:2.5,6.8,19,10.4,2.6", "M2.5,13.6 H21.5", "!circle:17.6,15.4,0.9" },
        ["network"] = new[] { "circle:12,12,9", "M3,12 H21", "M12,3 C8.3,6.6 8.3,17.4 12,21 C15.7,17.4 15.7,6.6 12,3 Z" },
        ["globe"] = new[] { "circle:12,12,9", "M3,12 H21", "M12,3 C8.3,6.6 8.3,17.4 12,21 C15.7,17.4 15.7,6.6 12,3 Z", "M4.5,7.5 H19.5 M4.5,16.5 H19.5" },
        ["trash"] = new[] { "M3.8,6.5 H20.2", "M9,6.5 V4.8 A1,1 0 0 1 10,3.8 H14 A1,1 0 0 1 15,4.8 V6.5", "M5.8,6.5 L6.9,19.3 A1.9,1.9 0 0 0 8.8,21 H15.2 A1.9,1.9 0 0 0 17.1,19.3 L18.2,6.5", "M10,10.3 V17.2", "M14,10.3 V17.2" },
        ["tag"] = new[] { "M3.5,4.8 V10.9 A1.8,1.8 0 0 0 4.03,12.17 L11.83,19.97 A1.8,1.8 0 0 0 14.37,19.97 L19.97,14.37 A1.8,1.8 0 0 0 19.97,11.83 L12.17,4.03 A1.8,1.8 0 0 0 10.9,3.5 H4.8 A1.3,1.3 0 0 0 3.5,4.8 Z", "!circle:7.9,7.9,1.45" },
        ["chevron.left"] = new[] { "M14.8,4.8 L7.6,12 L14.8,19.2" },
        ["chevron.right"] = new[] { "M9.2,4.8 L16.4,12 L9.2,19.2" },
        ["chevron.down"] = new[] { "M5,8.8 L12,15.8 L19,8.8" },
        ["chevron.up"] = new[] { "M5,15.2 L12,8.2 L19,15.2" },
        ["chevron.updown"] = new[] { "M7.5,9.5 L12,5 L16.5,9.5", "M7.5,14.5 L12,19 L16.5,14.5" },
        ["grid"] = new[] { "rect:3.5,3.5,7,7,1.8", "rect:13.5,3.5,7,7,1.8", "rect:3.5,13.5,7,7,1.8", "rect:13.5,13.5,7,7,1.8" },
        ["list"] = new[] { "M8.6,6 H20.5 M8.6,12 H20.5 M8.6,18 H20.5", "!circle:4.6,6,1.35", "!circle:4.6,12,1.35", "!circle:4.6,18,1.35" },
        ["columns"] = new[] { "rect:2.5,4.5,19,15,2.5", "M8.8,4.5 V19.5 M15.2,4.5 V19.5" },
        ["gallery"] = new[] { "rect:2.5,3.2,19,12.3,2.5", "!rect:2.5,17.6,5.3,3.2,1.1", "!rect:9.35,17.6,5.3,3.2,1.1", "!rect:16.2,17.6,5.3,3.2,1.1" },
        ["group"] = new[] { "rect:3,3.5,7.8,7,1.6", "rect:13.2,3.5,7.8,7,1.6", "rect:3,13.5,18,7,1.6" },
        ["share"] = new[] { "M12,2.8 V14.5", "M8,6.8 L12,2.8 L16,6.8", "M8.6,9.5 H6.6 A1.6,1.6 0 0 0 5,11.1 V19.4 A1.6,1.6 0 0 0 6.6,21 H17.4 A1.6,1.6 0 0 0 19,19.4 V11.1 A1.6,1.6 0 0 0 17.4,9.5 H15.4" },
        ["ellipsis.circle"] = new[] { "circle:12,12,9", "!circle:7.8,12,1.25", "!circle:12,12,1.25", "!circle:16.2,12,1.25" },
        ["ellipsis"] = new[] { "!circle:5,12,1.7", "!circle:12,12,1.7", "!circle:19,12,1.7" },
        ["magnifyingglass"] = new[] { "circle:10.4,10.4,6.6", "M15.2,15.2 L20.6,20.6" },
        ["wifi"] = new[] { "M2.81,9.81 A13,13 0 0 1 21.19,9.81", "M5.85,12.85 A8.7,8.7 0 0 1 18.15,12.85", "M8.89,15.89 A4.4,4.4 0 0 1 15.11,15.89", "!circle:12,19,1.55" },
        ["battery"] = new[] { "rect:1.5,7,19,10,3", "M22.3,10.4 V13.6" },
        ["controlcenter"] = new[] { "rect:2.5,3.8,19,7.4,3.7", "!circle:17.8,7.5,2.4", "rect:2.5,12.8,19,7.4,3.7", "!circle:6.2,16.5,2.4" },
        ["speaker"] = new[] { "!M3.5,9.2 H6.9 L11.8,5.1 V18.9 L6.9,14.8 H3.5 A0.6,0.6 0 0 1 2.9,14.2 V9.8 A0.6,0.6 0 0 1 3.5,9.2 Z", "M15,8.9 A4.4,4.4 0 0 1 15,15.1", "M17.8,6.3 A8.2,8.2 0 0 1 17.8,17.7" },
        ["speaker.0"] = new[] { "!M3.5,9.2 H6.9 L11.8,5.1 V18.9 L6.9,14.8 H3.5 A0.6,0.6 0 0 1 2.9,14.2 V9.8 A0.6,0.6 0 0 1 3.5,9.2 Z" },
        ["speaker.1"] = new[] { "!M3.5,9.2 H6.9 L11.8,5.1 V18.9 L6.9,14.8 H3.5 A0.6,0.6 0 0 1 2.9,14.2 V9.8 A0.6,0.6 0 0 1 3.5,9.2 Z", "M15,8.9 A4.4,4.4 0 0 1 15,15.1" },
        ["speaker.slash"] = new[] { "!M3.5,9.2 H6.9 L11.8,5.1 V18.9 L6.9,14.8 H3.5 A0.6,0.6 0 0 1 2.9,14.2 V9.8 A0.6,0.6 0 0 1 3.5,9.2 Z", "M15,9 L21,15 M21,9 L15,15" },
        ["sun"] = new[] { "circle:12,12,3.9", "M12,2.6 V4.6 M12,19.4 V21.4 M2.6,12 H4.6 M19.4,12 H21.4 M5.35,5.35 L6.75,6.75 M17.25,17.25 L18.65,18.65 M5.35,18.65 L6.75,17.25 M17.25,6.75 L18.65,5.35" },
        ["moon"] = new[] { "!M19.8,14.7 A8.4,8.4 0 1 1 9.3,4.2 A6.8,6.8 0 0 0 19.8,14.7 Z" },
        ["moon.outline"] = new[] { "M19.8,14.7 A8.4,8.4 0 1 1 9.3,4.2 A6.8,6.8 0 0 0 19.8,14.7 Z" },
        ["bluetooth"] = new[] { "M6.8,7.6 L17,16.6 L12,21 V3 L17,7.4 L6.8,16.4" },
        ["plus"] = new[] { "M12,5 V19 M5,12 H19" },
        ["minus"] = new[] { "M5,12 H19" },
        ["xmark"] = new[] { "M6.3,6.3 L17.7,17.7 M17.7,6.3 L6.3,17.7" },
        ["checkmark"] = new[] { "M5,12.6 L9.8,17.4 L19,6.6" },
        ["info"] = new[] { "circle:12,12,9", "M12,10.8 V16.6", "!circle:12,7.6,1.25" },
        ["eye"] = new[] { "M2.4,12 C4.9,7.2 8.3,5 12,5 C15.7,5 19.1,7.2 21.6,12 C19.1,16.8 15.7,19 12,19 C8.3,19 4.9,16.8 2.4,12 Z", "circle:12,12,3.3" },
        ["folder"] = new[] { "M3,7 A2,2 0 0 1 5,5 H9.4 L11.4,7 H19 A2,2 0 0 1 21,9 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 Z", "M3,9.6 H21" },
        ["person.circle"] = new[] { "circle:12,12,9", "circle:12,9.6,3.2", "M6.2,18.1 C7.4,15.9 9.4,14.9 12,14.9 C14.6,14.9 16.6,15.9 17.8,18.1" },
        ["display"] = new[] { "rect:2.5,3.8,19,12.8,2.2", "M8.8,20.6 H15.2 M12,16.6 V20.6" },
        ["laptop"] = new[] { "rect:4.2,5,15.6,10.6,1.6", "M2,18.6 H22", "M2.6,18.6 L4.2,15.6 M21.4,18.6 L19.8,15.6" },
        ["keyboard"] = new[] { "rect:2,6,20,12,2.2", "M7,14.8 H17", "!circle:6.2,10,0.9", "!circle:9.4,10,0.9", "!circle:12.6,10,0.9", "!circle:15.8,10,0.9", "!circle:19,10,0.9" },
        ["circle.lefthalf"] = new[] { "circle:12,12,9", "!M12,3 A9,9 0 0 0 12,21 Z" },
        ["dock"] = new[] { "rect:2.5,4,19,16,2.6", "!rect:6.3,15.6,11.4,2,1" },
        ["wallpaper"] = new[] { "rect:2.5,5,19,14,2.4", "M2.9,16.8 C6,12.6 9.5,12.2 12.4,14.4 C15,16.4 18.4,15.6 21.1,12.6" },
        ["bell"] = new[] { "M5.8,16.6 V11 A6.2,6.2 0 0 1 18.2,11 V16.6 L19.7,18.2 H4.3 Z", "M10,20.6 A2,2 0 0 0 14,20.6" },
        ["power"] = new[] { "M12,2.8 V11", "M7.3,5.8 A8.2,8.2 0 1 0 16.7,5.8" },
        ["lock"] = new[] { "rect:5,10.5,14,10.3,2.2", "M8.1,10.5 V7.6 A3.9,3.9 0 0 1 15.9,7.6 V10.5" },
        ["sidebar.left"] = new[] { "rect:2.5,4,19,16,2.6", "M9,4 V20", "M4.8,7.8 H6.8 M4.8,10.6 H6.8 M4.8,13.4 H6.8" },
        ["terminal"] = new[] { "rect:2.5,4,19,16,2.6", "M6.6,9 L9.6,12 L6.6,15", "M11.6,15 H16.4" },
        ["hand"] = new[] { "M8,13 V5.8 A1.4,1.4 0 0 1 10.8,5.8 V11.4 M10.8,5 A1.4,1.4 0 0 1 13.6,5 V11.4 M13.6,5.6 A1.4,1.4 0 0 1 16.4,5.6 V11.8 M16.4,8 A1.4,1.4 0 0 1 19.2,8 V14 C19.2,18 16.6,21 12.8,21 C10.2,21 8.6,19.8 7.2,17.8 L4.6,13.9 A1.4,1.4 0 0 1 6.9,12.3 L8,13.8" },
        ["square.split"] = new[] { "rect:2.5,4,19,16,2.6", "M12,4 V20" },
        ["accessibility"] = new[] { "circle:12,12,9", "!circle:12,7.4,1.5", "M7.5,10.3 H16.5", "M12,10.3 V14.2 L9.6,18.2 M12,14.2 L14.4,18.2" },
        ["arrow.clockwise"] = new[] { "M19,12 A7,7 0 1 1 16.5,6.6", "M17,3.4 V7.1 H13.3" },
        ["star"] = new[] { "M12,3.2 L14.6,8.9 L20.8,9.6 L16.2,13.8 L17.5,19.9 L12,16.8 L6.5,19.9 L7.8,13.8 L3.2,9.6 L9.4,8.9 Z" },
        ["square.and.pencil"] = new[] { "M11,4.5 H6.5 A2,2 0 0 0 4.5,6.5 V17.5 A2,2 0 0 0 6.5,19.5 H17.5 A2,2 0 0 0 19.5,17.5 V13", "M17.4,3.6 L20.4,6.6 L12,15 L8.6,15.4 L9,12 Z" },
        ["command"] = new[] { "M9,9 H15 V15 H9 Z", "M9,9 V6.8 A2.2,2.2 0 1 0 6.8,9 Z", "M15,9 H17.2 A2.2,2.2 0 1 0 15,6.8 Z", "M9,15 H6.8 A2.2,2.2 0 1 0 9,17.2 Z", "M15,15 V17.2 A2.2,2.2 0 1 0 17.2,15 Z" },
        ["airplay"] = new[] { "M6.5,17 H4.5 A2,2 0 0 1 2.5,15 V6 A2,2 0 0 1 4.5,4 H19.5 A2,2 0 0 1 21.5,6 V15 A2,2 0 0 1 19.5,17 H17.5", "!M12,14 L17,20.5 H7 Z" },
        ["focus"] = new[] { "!M19.8,14.7 A8.4,8.4 0 1 1 9.3,4.2 A6.8,6.8 0 0 0 19.8,14.7 Z" },
        ["battery.charging"] = new[] { "rect:1.5,7,19,10,3", "M22.3,10.4 V13.6", "!M12,8.3 L7.8,12.6 H10.9 L9.8,15.8 L14.2,11.4 H11.1 Z" },
        ["arrow.up.left.and.arrow.down.right"] = new[] { "M4.5,10 V4.5 H10", "M4.5,4.5 L10.5,10.5", "M19.5,14 V19.5 H14", "M19.5,19.5 L13.5,13.5" },
        ["rectangle.stack"] = new[] { "rect:3,8,18,12,2.2", "M5.5,5 H18.5", "M7.5,2.5 H16.5" },
        ["quicklook"] = new[] { "circle:10.5,10.5,6.3", "M15.1,15.1 L20.4,20.4", "M7.4,10.5 C8.4,8.8 9.4,8.1 10.5,8.1 C11.6,8.1 12.6,8.8 13.6,10.5 C12.6,12.2 11.6,12.9 10.5,12.9 C9.4,12.9 8.4,12.2 7.4,10.5 Z" },
        ["gear"] = new[] { "@gear" },
        ["recents"] = new[] { "circle:12,12,9", "M12,7 V12 L15.5,14.2" },
        ["airdrop"] = new[] { "circle:12,13,1.6", "M8.6,16.6 A5,5 0 1 1 15.4,16.6", "M5.8,19.2 A8.8,8.8 0 1 1 18.2,19.2" },
        ["cart"] = new[] { "M2.8,4 H5.4 L7.8,15.4 H18.4 L20.6,7.4 H6.4", "!circle:9,19.2,1.5", "!circle:17,19.2,1.5" },
        ["arrow.right.circle"] = new[] { "circle:12,12,9", "M7.5,12 H16.2", "M12.6,8.2 L16.4,12 L12.6,15.8" },
    };

    static readonly Dictionary<string, (Geometry g, bool fill)[]> Cache = new();

    public static bool Exists(string name) => name != null && Defs.ContainsKey(name);

    public static (Geometry g, bool fill)[] Get(string name)
    {
        if (name == null) return Array.Empty<(Geometry, bool)>();
        if (Cache.TryGetValue(name, out var c)) return c;
        if (!Defs.TryGetValue(name, out var parts)) return Array.Empty<(Geometry, bool)>();
        var list = new List<(Geometry, bool)>();
        foreach (var raw in parts)
        {
            bool fill = raw.StartsWith("!");
            string p = fill ? raw[1..] : raw;
            Geometry g;
            if (p == "@gear") g = GearOutline(12, 12, 9.2, 7.4, 8, 3.1);
            else if (p.StartsWith("circle:"))
            {
                var v = Nums(p[7..]);
                g = new EllipseGeometry(new Point(v[0], v[1]), v[2], v[2]);
            }
            else if (p.StartsWith("rect:"))
            {
                var v = Nums(p[5..]);
                g = new RectangleGeometry(new Rect(v[0], v[1], v[2], v[3]), v[4], v[4]);
            }
            else g = Geometry.Parse(p);
            g.Freeze();
            list.Add((g, fill));
        }
        var arr = list.ToArray();
        Cache[name] = arr;
        return arr;
    }

    static double[] Nums(string s) => s.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Gear outline with rounded teeth and a centre hole (for stroking).</summary>
    public static Geometry GearOutline(double cx, double cy, double rOuter, double rInner, int teeth, double hole)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            int n = teeth * 4;
            for (int i = 0; i <= n; i++)
            {
                double a = (i / (double)n) * Math.PI * 2 - Math.PI / 2;
                int phase = i % 4;
                double r = phase is 1 or 2 ? rOuter : rInner;
                var pt = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
                if (i == 0) ctx.BeginFigure(pt, true, true); else ctx.LineTo(pt, true, true);
            }
            ctx.BeginFigure(new Point(cx + hole, cy), true, true);
            ctx.ArcTo(new Point(cx - hole, cy), new Size(hole, hole), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new Point(cx + hole, cy), new Size(hole, hole), 0, false, SweepDirection.Clockwise, true, true);
        }
        sg.Freeze();
        return sg;
    }

    /// <summary>Renders a symbol into a DrawingImage (used in menus and tiles).</summary>
    public static DrawingImage Image(string name, Brush brush, double stroke = 1.7, double size = 24)
    {
        var dg = new DrawingGroup();
        var pen = new Pen(brush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        foreach (var (g, fill) in Get(name))
            dg.Children.Add(new GeometryDrawing(fill ? brush : null, fill ? null : pen, g));
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }
}

/// <summary>Draws an SF Symbols–style glyph, inheriting the text foreground colour.</summary>
public class SymbolIcon : FrameworkElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(string), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SymbolIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillPartsProperty = DependencyProperty.Register(nameof(FillAll), typeof(bool), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Symbol { get => (string)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public bool FillAll { get => (bool)GetValue(FillPartsProperty); set => SetValue(FillPartsProperty, value); }

    public SymbolIcon()
    {
        Width = 16; Height = 16;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var parts = Sym.Get(Symbol);
        if (parts.Length == 0) return;
        double s = Math.Min(ActualWidth, ActualHeight) / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        var brush = Foreground ?? Brushes.Black;
        var pen = new Pen(brush, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var (g, fill) in parts)
        {
            if (fill || FillAll) dc.DrawGeometry(brush, null, g);
            else dc.DrawGeometry(null, pen, g);
        }
        dc.Pop(); dc.Pop();
    }
}
