using Autodesk.Revit.DB;
using System.Windows;
using System.Windows.Controls;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

internal sealed class ImportedMaterialDialog : Window
{
    internal sealed record Choice(string Name,byte Red,byte Green,byte Blue);
    internal Choice? Selected {get;private set;}
    internal ImportedMaterialDialog(Document doc,string familyName)
    {
        Title="Materiales y apariencia";Width=540;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var panel=new StackPanel {Margin=new Thickness(18)};Content=panel;
        void Text(string value)=>panel.Children.Add(new TextBlock {Text=value,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5)});
        var mode=new ComboBox {ItemsSource=new[]{"Conservar apariencia original","Aplicar color RGB uniforme","Copiar color gráfico de material disponible"},SelectedIndex=0};panel.Children.Add(mode);
        Text("Solo material gráfico persistente en Sombreado. No modifica geometría ni genera IA. No configura apariencia Realista ni propiedades físicas/térmicas.");
        var regions=ImportedMaterials.Inspect(doc);
        Text($"Regiones detectadas: {regions.Count}. No se identifican automáticamente asiento, respaldo ni patas. Los materiales explícitos se editan solo si pertenecen a esta importación nueva.");
        Text("Material disponible en esta familia (se copia únicamente su color):");
        var available=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().OrderBy(m=>m.Name).ToArray();
        var source=new ComboBox {ItemsSource=available.Select(m=>m.Name).ToArray(),SelectedIndex=available.Length>0?0:-1};panel.Children.Add(source);
        Text("Nombre identificable del material:");var name=new TextBox {Text=familyName+"_Color"};panel.Children.Add(name);
        Text("Rojo, Verde, Azul (0–255, separados por comas):");var rgb=new TextBox {Text="0,80,220"};panel.Children.Add(rgb);
        source.IsEnabled=false;name.IsEnabled=false;rgb.IsEnabled=false;
        mode.SelectionChanged+=(_,_)=>{source.IsEnabled=mode.SelectedIndex==2;rgb.IsEnabled=mode.SelectedIndex==1;name.IsEnabled=mode.SelectedIndex!=0;};
        var error=new TextBlock {TextWrapping=TextWrapping.Wrap};panel.Children.Add(error);
        var accept=new Button {Content="Continuar y guardar como nuevo RFA",Margin=new Thickness(0,10,0,5),Padding=new Thickness(8)};panel.Children.Add(accept);
        accept.Click+=(_,_)=>{try{
            if(mode.SelectedIndex!=0){ObjInspection.ValidateName(name.Text.Trim());byte r,g,b;
                if(mode.SelectedIndex==2){if(source.SelectedIndex<0)throw new ArgumentException("Seleccione un material.");var c=available[source.SelectedIndex].Color;r=c.Red;g=c.Green;b=c.Blue;}
                else{var parts=rgb.Text.Split(',');if(parts.Length!=3 || !byte.TryParse(parts[0].Trim(),out r) || !byte.TryParse(parts[1].Trim(),out g) || !byte.TryParse(parts[2].Trim(),out b))throw new ArgumentException("Indique RGB, por ejemplo 0,80,220.");}
                Selected=new(name.Text.Trim(),r,g,b);
            }DialogResult=true;
        }catch(Exception ex){error.Text=ex.Message;}};
        var cancel=new Button {Content="Cancelar creación de RFA",IsCancel=true,Padding=new Thickness(8)};panel.Children.Add(cancel);
    }
}
