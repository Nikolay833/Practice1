using System.Collections.Generic;
using Dropshelf.Shelf;

namespace Dropshelf.Services;

/// <summary>
/// Tracks drags that start inside Dropshelf so our own drop targets can tell them apart from
/// external drags and shake detection can ignore them.
/// </summary>
internal static class DragState
{
    public static ItemStore? Origin { get; private set; }
    public static IReadOnlyList<ShelfItem> Items { get; private set; } = [];
    public static bool IsActive => Origin != null;

    public static void Begin(ItemStore origin, IReadOnlyList<ShelfItem> items)
    {
        Origin = origin;
        Items = items;
    }

    public static void End()
    {
        Origin = null;
        Items = [];
    }
}
