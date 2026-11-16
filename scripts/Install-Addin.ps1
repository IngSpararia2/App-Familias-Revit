param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$registration = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Autodesk\Revit\Addins\2027'
$manifestPath = Join-Path $registration 'BIMFamilyAI.addin'
$deployment = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BIM Family AI\Revit2027'
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Cierre Revit antes de instalar, actualizar o desinstalar.' }
if ($Uninstall) {
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
    # Remove only known application files; never recursively remove a shared directory.
    foreach ($name in @('BimFamilyAI.Revit.dll', 'BimFamilyAI.Core.dll', 'BimFamilyAI.Revit.deps.json')) {
        $file = Join-Path $deployment $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    Write-Output 'Complemento desinstalado. Las familias generadas se conservan.'
    return
}
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\BimFamilyAI.Revit\BimFamilyAI.Revit.csproj'
Push-Location $root
try {
    dotnet build $project -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación. No se actualizó el manifiesto.' }
} finally { Pop-Location }
$source = Join-Path $root "src\BimFamilyAI.Revit\bin\x64\$Configuration\net10.0-windows"
New-Item -ItemType Directory -Path $deployment, $registration -Force | Out-Null
foreach ($name in @('BimFamilyAI.Revit.dll', 'BimFamilyAI.Core.dll', 'BimFamilyAI.Revit.deps.json')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $deployment $name) -Force
}
$assembly = [System.Security.SecurityElement]::Escape((Join-Path $deployment 'BimFamilyAI.Revit.dll'))
[xml]$manifest = @"
<RevitAddIns>
  <AddIn Type="Command">
    <Assembly>$assembly</Assembly>
    <AddInId>2FC87746-282E-446B-8EC1-7E9A969EB97E</AddInId>
    <FullClassName>BimFamilyAI.Revit.GenerateTestFamilyCommand</FullClassName>
    <Text>BIM Family AI — Generar familia de prueba</Text>
    <VendorId>BFAI</VendorId>
    <VendorDescription>BIM Family AI</VendorDescription>
    <Description>Generar familia de prueba: cubo nativo de 500 mm.</Description>
  </AddIn>
  <AddIn Type="Command">
    <Assembly>$assembly</Assembly>
    <AddInId>A6E63248-4A84-4A47-9163-6F56478AB8C2</AddInId>
    <FullClassName>BimFamilyAI.Revit.GenerateParametricFamilyCommand</FullClassName>
    <Text>BIM Family AI — Generar familia paramétrica</Text>
    <VendorId>BFAI</VendorId>
    <VendorDescription>BIM Family AI</VendorDescription>
    <Description>Prisma con dimensiones y material de tipo editables.</Description>
  </AddIn>
  <AddIn Type="Command">
    <Assembly>$assembly</Assembly>
    <AddInId>46F40841-7C37-4705-96D1-EE848D8762FB</AddInId>
    <FullClassName>BimFamilyAI.Revit.ImportObjFamilyCommand</FullClassName>
    <Text>BIM Family AI — Importar OBJ como familia</Text>
    <VendorId>BFAI</VendorId>
    <VendorDescription>BIM Family AI</VendorDescription>
    <Description>Importación OBJ nativa con unidades, escala y posición verificadas.</Description>
  </AddIn>
</RevitAddIns>
"@
$manifest.Save($manifestPath)
foreach ($entry in $manifest.RevitAddIns.AddIn) {
    if (!(Test-Path -LiteralPath $entry.Assembly)) { throw 'El ensamblado registrado no existe.' }
}
Write-Output "Complemento instalado: $manifestPath"
