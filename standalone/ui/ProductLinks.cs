using ArtSport.ArtVpn.Common;

namespace ArtSport.ArtVpn.Ui;

internal static class ProductLinks
{
    internal const string QuattroReferral = QuattroRecommendation.Url;

    internal static LinkLabel QuattroLink(string name, string text, Point location, Action<string>? openWebsite = null)
    {
        var link = new LinkLabel
        {
            Name = name,
            Text = text,
            AutoSize = true,
            Location = location,
            LinkColor = Palette.Blue,
            ActiveLinkColor = Palette.Blue,
            Font = new Font("Segoe UI Semibold", 9.2f)
        };
        link.LinkClicked += (_, _) => (openWebsite ?? QuattroRecommendation.OpenWebsite)(QuattroReferral);
        return link;
    }
}
