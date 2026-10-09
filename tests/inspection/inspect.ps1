param([string]$Assembly='client',[string]$TypePattern='',[string]$MethodPattern='',[switch]$Body)
$root='C:\Programas\Steam\steamapps\common\SPT'
Add-Type -Path "$root/SPT_Runtime/Mono.Cecil.dll"
$path=if($Assembly -eq 'server'){"$root/SPT_Runtime/SPTarkov.Server.Core.dll"}else{"$root/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll"}
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
function Types($ts){foreach($t in $ts){$t;if($t.HasNestedTypes){Types $t.NestedTypes}}}
foreach($t in (Types $a.MainModule.Types) | Where-Object {$_.FullName -match $TypePattern}){
 if(!$MethodPattern){"TYPE $($t.FullName)";$t.Fields | ForEach-Object {"FIELD $($_.FieldType) $($_.Name)"};$t.Properties | ForEach-Object {"PROP $($_.PropertyType) $($_.Name)"}}
 foreach($m in $t.Methods | Where-Object {$_.Name -match $MethodPattern}){
 "METHOD $m"
 if($Body -and $m.HasBody){$m.Body.Instructions | ForEach-Object {$_.ToString()}}
 }
}
$a.Dispose()
