[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference='Stop'
if([IO.File]::Exists($ReportPath)){throw 'Choose a fresh PE inspection report.'}
$data=[IO.File]::ReadAllBytes($ExecutablePath)
function U16([int]$offset){[BitConverter]::ToUInt16($data,$offset)}
function U32([int]$offset){[BitConverter]::ToUInt32($data,$offset)}
function U64([int]$offset){[BitConverter]::ToUInt64($data,$offset)}
function CString([int]$offset){
    $end=$offset
    while($end -lt $data.Length -and $data[$end] -ne 0 -and $end-$offset -le 512){$end++}
    if($offset -lt 0 -or $end -ge $data.Length -or $end-$offset -gt 512){throw 'Invalid PE import string.'}
    [Text.Encoding]::ASCII.GetString($data,$offset,$end-$offset)
}
if($data.Length -lt 512 -or (U16 0) -ne 0x5a4d){throw 'Native loader must be a real PE image.'}
$pe=U32 60
if((U32 $pe) -ne 0x4550 -or (U16 ($pe+4)) -ne 0x8664){throw 'AMD64 PE required.'}
$optional=$pe+24;$optionalSize=U16 ($pe+20);$sectionCount=U16 ($pe+6)
if((U16 $optional) -ne 0x20b -or $optionalSize -lt 240 -or (U16 ($optional+68)) -ne 2){throw 'PE32+ Windows GUI subsystem required.'}
$sections=@(for($i=0;$i -lt $sectionCount;$i++){
    $offset=$optional+$optionalSize+$i*40
    [pscustomobject]@{rva=U32 ($offset+12);size=U32 ($offset+16);raw=U32 ($offset+20);flags=U32 ($offset+36)}
})
function Map-Rva([uint32]$rva){
    foreach($section in $sections){
        if($rva -ge $section.rva -and $rva-$section.rva -lt $section.size){
            $value=[long]$section.raw+$rva-$section.rva
            if($value -ge $data.Length){throw 'PE RVA outside actual bytes.'}
            return [int]$value
        }
    }
    throw 'PE RVA not mapped.'
}
$importRva=U32 ($optional+120)
if($importRva -eq 0){throw 'Expected reviewed KERNEL32 static imports.'}
$descriptor=Map-Rva $importRva;$imports=@()
for($i=0;$i -lt 16;$i++){
    $offset=$descriptor+$i*20;$nameRva=U32 ($offset+12)
    if($nameRva -eq 0){break}
    $dll=CString (Map-Rva $nameRva);$lookupRva=U32 $offset
    if($lookupRva -eq 0){$lookupRva=U32 ($offset+16)}
    $lookup=Map-Rva $lookupRva;$names=@()
    for($j=0;$j -lt 128;$j++){
        $thunk=U64 ($lookup+$j*8)
        if($thunk -eq 0){break}
        if($thunk -gt [uint32]::MaxValue){throw 'Unexpected native ordinal import.'}
        $names+=CString ((Map-Rva ([uint32]$thunk))+2)
    }
    $imports+=[ordered]@{dll=$dll;functions=$names}
}
$expected=@('ExitProcess','GetCurrentProcessId','GetCurrentThreadId','GetEnvironmentVariableW','GetLastError','GetModuleHandleW','GetProcAddress','GetStdHandle','LoadLibraryExW','SetLastError','WriteFile')
if($imports.Count -ne 1 -or $imports[0].dll -ine 'KERNEL32.dll' -or
    (Compare-Object ($expected|Sort-Object) ($imports[0].functions|Sort-Object))){throw 'Native loader static imports differ from the reviewed KERNEL32-only set.'}
$tls=U32 ($optional+112+9*8);$delay=U32 ($optional+112+13*8);$entry=U32 ($optional+16)
if($tls -ne 0 -or $delay -ne 0){throw 'Unexpected native TLS or delayed imports.'}
if(-not ($sections|Where-Object{$entry -ge $_.rva -and $entry-$_.rva -lt $_.size -and ($_.flags -band 0x20000000)})){throw 'Native entry must be executable.'}
[ordered]@{schema_version=1;producer='派蒙';status='passed_static_PE_only';sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant();
    bytes=$data.Length;machine='AMD64';subsystem=2;entry_rva=$entry;imports=$imports;tls_rva=$tls;delay_import_rva=$delay;
    child_execution='not_run';clipboard_calls='none_in_source';full_isolation='not_claimed'}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
Write-Host "Native KERNEL32-only PE inspection passed: $ReportPath"
