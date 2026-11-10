using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Winisland.Controls;

// Deferred template setters can lose the namespace context of a string attached
// property path. Bind to the registered DP itself so rail updates always resolve.
public sealed class SmoothValueBinding : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Path = new PropertyPath("(0)", SmoothValue.DisplayValueProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
        Mode = BindingMode.OneWay
    }.ProvideValue(serviceProvider);
}
