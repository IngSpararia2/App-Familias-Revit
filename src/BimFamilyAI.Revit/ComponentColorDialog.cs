using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

/// <summary>Optional shaded RGB per topological component; no semantic names are inferred.</summary>
internal sealed class ComponentColorDialog : Window
{
    internal IReadOnlyList<ComponentColor> SelectedColors { get; private set; } = [];

    internal ComponentColorDialog(IReadOnlyList<ObjComponent> components, IReadOnlyList<ComponentColor> current)
    {
        Title = "BIM Family AI — Colores por componente";
        Width = Math.Min(590, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(630, SystemParameters.WorkArea.Height * 0.9);
        MinWidth = 440; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
        var panel = new StackPanel { Margin = new Thickness(18) };
        scroll.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Colores gráficos independientes", FontSize = 18,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Componente significa malla desconectada por índices, sin nombre semántico. " +
            "Desmarque una pieza para conservar su apariencia original. RGB afecta a Sombreado; no añade texturas.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var rows = new List<(int Number, CheckBox Apply, TextBox Rgb)>();
        foreach (var component in components)
        {
            var prior = current.SingleOrDefault(c => c.Number == component.Number);
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            panel.Children.Add(row);
            var apply = new CheckBox { Content = $"Componente_{component.Number:000} · {component.Faces} caras",
                IsChecked = prior is not null };
            row.Children.Add(apply);
            var rgb = new TextBox { Text = prior is null ? "180,180,180" : $"{prior.Red},{prior.Green},{prior.Blue}",
                MinHeight = 27, Margin = new Thickness(16, 4, 0, 0), IsEnabled = prior is not null };
            row.Children.Add(rgb);
            apply.Checked += (_, _) => rgb.IsEnabled = true;
            apply.Unchecked += (_, _) => rgb.IsEnabled = false;
            rows.Add((component.Number, apply, rgb));
        }
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var accept = new Button { Content = "Aplicar selección al nuevo RFA", Padding = new Thickness(10),
            Margin = new Thickness(0, 10, 0, 5) };
        panel.Children.Add(accept);
        accept.Click += (_, _) =>
        {
            var colors = new List<ComponentColor>();
            foreach (var row in rows.Where(r => r.Apply.IsChecked == true))
            {
                var parts = row.Rgb.Text.Split(',');
                if (parts.Length != 3 || !byte.TryParse(parts[0].Trim(), out byte red) ||
                    !byte.TryParse(parts[1].Trim(), out byte green) || !byte.TryParse(parts[2].Trim(), out byte blue))
                { error.Text = $"RGB inválido en Componente_{row.Number:000}. Use tres números de 0 a 255."; return; }
                colors.Add(new ComponentColor(row.Number, red, green, blue));
            }
            SelectedColors = colors;
            DialogResult = true;
        };
        panel.Children.Add(new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(8) });
    }
}
