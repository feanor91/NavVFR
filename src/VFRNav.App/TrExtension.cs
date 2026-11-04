using System.Windows.Markup;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Texte traduit dans le XAML : <c>Text="{local:Tr Cle}"</c> lit la clé dans les ressources (Strings.resx, Strings.en.resx).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
