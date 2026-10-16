using System.Windows;

namespace NetCheck.Ui;

/// <summary>
/// 调色板资源字典，只装**初始（浅色）**色值。
///
/// 为什么画刷在代码里建而不是在 App.xaml 里写 `&lt;SolidColorBrush&gt;`：
/// XAML 声明的画刷和代码声明的画刷殊途同归 —— 只要被任何 Style 的 Setter 引用到，
/// 样式封印时就会把这个对象冻结（`StaticResource` 不克隆，字典里那个对象被连带冻结）。
/// 冻结的画刷改不了 `Color`。
///
/// 所以换主题不能在画刷上改色，只能**换掉字典里的条目**、配合 `{DynamicResource}`
/// 的引用收到变更通知后自己取新值 —— 这就是 <see cref="Theme.WritePalette"/> 做的事。
/// 这个字典存在的意义只是「让样式在启动时就有值可取」；真正切主题时它里面的条目
/// 会被 `Application.Resources` 顶层的同名条目覆盖。
/// </summary>
public sealed class PaletteDictionary : ResourceDictionary
{
    public PaletteDictionary() => Theme.Fill(this);
}
