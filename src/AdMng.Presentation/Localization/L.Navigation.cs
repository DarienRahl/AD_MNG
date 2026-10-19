namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Very large containers shown as a virtual list (VLV, server sorted, read while scrolling).</summary>
    public static class ListVirtual
    {
        /// <summary>Status bar badge of a virtual list.</summary>
        public const string Badge = "List.Virtual.Badge";

        /// <summary>Tooltip of the badge: what the virtual mode means for the operator.</summary>
        public const string BadgeToolTip = "List.Virtual.BadgeToolTip";

        /// <summary>{0} = number of objects of the server-filtered list.</summary>
        public const string FilterMatches = "List.Virtual.FilterMatches";

        /// <summary>{0} = objects, {1} = sort column, {2} = time of the read.</summary>
        public const string Summary = "List.Virtual.Summary";
    }
}
