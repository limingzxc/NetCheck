using System.Windows.Media;
using System.Windows.Media.Effects;

namespace NetCheck.Ui;

/// <summary>
/// 卡片投影。「现代柔和」的层次靠两件事：一层很淡的投影 + 一条浅边线。
///
/// **投影颜色与主题无关，始终用黑色、只调不透明度。**
/// 因为 <see cref="DropShadowEffect.Color"/> 是普通属性、不是画刷，没法像调色板那样
/// 就地改色刷新 —— 一旦把主题色写进去，切换主题后已有卡片的投影就会留着旧色。
/// 用黑色则深浅两套主题都能用：浅色下 8% 的黑落在浅灰底上是一层干净的灰影，
/// 深色下加深到 55%，而深色界面的层次本来就主要靠边线，投影只是补一点纵深。
/// </summary>
public static class Elevation
{
    /// <summary>外层卡片：很淡、略微下沉，只为了把卡片从背景上「托」起来一点。</summary>
    public static DropShadowEffect Card() => Make(blur: 16, depth: 1, opacity: Theme.IsDark ? 0.55 : 0.08);

    /// <summary>可点的指标块：比卡片再轻一档，悬停时换成主色描边而不是加重投影。</summary>
    public static DropShadowEffect Tile() => Make(blur: 12, depth: 1, opacity: Theme.IsDark ? 0.40 : 0.06);

    private static DropShadowEffect Make(double blur, double depth, double opacity) => new()
    {
        BlurRadius = blur,
        ShadowDepth = depth,
        Direction = 270,
        Color = Colors.Black,
        Opacity = opacity,
        // 大量卡片同屏时，质量优先的渲染会明显拖慢滚动；这里观感差异看不出来。
        RenderingBias = RenderingBias.Performance,
    };
}
