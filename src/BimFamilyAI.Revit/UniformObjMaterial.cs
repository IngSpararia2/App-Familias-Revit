using System.Globalization;
using System.IO;
using System.Text;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

/// <summary>Changes only OBJ material directives; leaves geometry records verbatim.</summary>
internal sealed class UniformObjMaterial : IDisposable
{
    private readonly string directory;
    internal string Path {get;}
    private UniformObjMaterial(string source)
    {
        directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"BFAI-Material-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);Path=System.IO.Path.Combine(directory,System.IO.Path.GetFileName(source));
    }
    internal static UniformObjMaterial Prepare(string source,byte r,byte g,byte b)
    {
        ObjInspection.Inspect(source);
        var prepared=new UniformObjMaterial(source);
        try
        {
            string material="BFAI_Uniform_"+Guid.NewGuid().ToString("N");
            File.WriteAllText(System.IO.Path.Combine(prepared.directory,"uniform.mtl"),$"newmtl {material}\nKd {(r/255d).ToString("R",CultureInfo.InvariantCulture)} {(g/255d).ToString("R",CultureInfo.InvariantCulture)} {(b/255d).ToString("R",CultureInfo.InvariantCulture)}\nd 1\n",new UTF8Encoding(false));
            using(var writer=new StreamWriter(prepared.Path,false,new UTF8Encoding(false)))
            {
                writer.WriteLine("mtllib uniform.mtl");writer.WriteLine("usemtl "+material);
                foreach(string line in File.ReadLines(source))
                {
                    var trimmed=line.TrimStart();int end=trimmed.IndexOfAny([' ','\t']);string directive=end<0?trimmed:trimmed[..end];
                    if(directive is not ("mtllib" or "usemtl"))writer.WriteLine(line);
                }
            }
            if(ObjInspection.Inspect(prepared.Path)!=ObjInspection.Inspect(source))throw new IOException("La copia con material cambió las dimensiones del OBJ.");
            return prepared;
        }
        catch{prepared.Dispose();throw;}
    }
    public void Dispose()=>Directory.Delete(directory,true);
}
