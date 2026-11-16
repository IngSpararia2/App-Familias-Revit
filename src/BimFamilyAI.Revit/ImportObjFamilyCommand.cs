using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimFamilyAI.Core;
using Microsoft.Win32;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace BimFamilyAI.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class ImportObjFamilyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var file = new OpenFileDialog { Title = "Seleccionar OBJ local", Filter = "Modelo OBJ (*.obj)|*.obj", CheckFileExists = true };
            if (file.ShowDialog() != true) return Result.Cancelled;
            ObjInspection.Inspect(file.FileName);
            var request = Configure(file.FileName, commandData.Application.MainWindowHandle);
            if (request is null) return Result.Cancelled;
            return FamilyFileWorkflow.ExecuteFamily(commandData, ref message, false, request);
        }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show("BIM Family AI — OBJ", message); return Result.Failed; }
    }

    private static ObjImportRequest? Configure(string path, IntPtr owner)
    {
        var window = new Window { Title = "Importar OBJ como familia", Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(16) }; window.Content = panel;
        new System.Windows.Interop.WindowInteropHelper(window).Owner = owner;
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0,8,0,3), TextWrapping = TextWrapping.Wrap });
        Label("Unidades del OBJ (selección obligatoria)");
        var units = new ComboBox { ItemsSource = new[] { "Milímetros", "Centímetros", "Metros", "Pulgadas", "Pies" } }; panel.Children.Add(units);
        Label("Eje vertical de origen"); var axis = new ComboBox { ItemsSource = new[] { "Z", "Y", "X" }, SelectedIndex = 0 }; panel.Children.Add(axis);
        Label("Altura objetivo en mm (opcional; escala uniforme)"); var height = new TextBox(); panel.Children.Add(height);
        Label("Nombre de familia (sin extensión)"); var name = new TextBox { Text = System.IO.Path.GetFileNameWithoutExtension(path) }; panel.Children.Add(name);
        Label("Descripción (opcional)"); var description = new TextBox(); panel.Children.Add(description);
        Label("Ancho, Profundidad y Altura serán datos informativos; no controlarán la malla.");
        var button = new Button { Content = "Generar RFA", Margin = new Thickness(0,12,0,0), Padding = new Thickness(8), IsDefault = true }; panel.Children.Add(button);
        ObjImportRequest? result = null;
        button.Click += (_, _) =>
        {
            try
            {
                if (units.SelectedIndex < 0) throw new InvalidOperationException("Seleccione las unidades originales.");
                ObjInspection.ValidateName(name.Text);
                double? target = null;
                if (!string.IsNullOrWhiteSpace(height.Text))
                {
                    if (!double.TryParse(height.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || value <= 0)
                        throw new InvalidOperationException("Escriba una altura positiva en mm.");
                    target = value;
                }
                result = new(path, (ObjUnit)units.SelectedIndex, (ObjVerticalAxis)axis.SelectedIndex, target, name.Text, description.Text);
                window.DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Revisar configuración"); }
        };
        return window.ShowDialog() == true ? result : null;
    }
}
