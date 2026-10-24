#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DumpPath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$DumpPath=[IO.Path]::GetFullPath($DumpPath)
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $OutputPath){throw 'Existing evidence is preserved.'}
# Pure file-format parser. No DbgHelp load, process access, debugger, upload,
# raw-memory/stack dump, regex string scan, or user-data discovery.
$file=[IO.File]::Open($DumpPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$reader=[IO.BinaryReader]::new($file)
function Bounds([long]$offset,[long]$length){if($offset -lt 0 -or $length -lt 0 -or $offset -gt $file.Length-$length){throw 'Minidump range outside file.'}}
function U32([long]$offset){Bounds $offset 4;$file.Position=$offset;return $reader.ReadUInt32()}
function U64([long]$offset){Bounds $offset 8;$file.Position=$offset;return $reader.ReadUInt64()}
function U16([long]$offset){Bounds $offset 2;$file.Position=$offset;return $reader.ReadUInt16()}
function Bytes([long]$offset,[int]$length){Bounds $offset $length;$file.Position=$offset;return ,$reader.ReadBytes($length)}
function Read-StringRva([long]$rva,[bool]$unicode=$false){
    if($rva -eq 0){return ''};$size=U32 $rva
    if($size -gt 8192 -or ($unicode -and $size%2)){throw 'Unbounded or invalid metadata string.'}
    $raw=Bytes ($rva+4) $size
    if($unicode){return [Text.Encoding]::Unicode.GetString($raw)}
    return [Text.Encoding]::UTF8.GetString($raw).TrimEnd([char]0)
}
function Hex64([uint64]$value){return '0x'+$value.ToString('x16')}
function Hex32([uint32]$value){return '0x'+$value.ToString('x8')}
$reasonKeys='(?i)^(fatal[-_]?(log|message)|last[-_]?(error|fatal)|crash[-_]?(reason|message)|reason|check|check[-_]?failure|dcheck|dcheck[-_]?failure|ptype|process[-_]type|ver|version|bad_message_reason|bad-message-reason|mojo-message-error|rfhi_can_commit_failure_reason|hung-reason)$'
$annotations=[Collections.Generic.List[object]]::new()
$annotationStats=[ordered]@{dictionary_keys_examined=0;typed_keys_examined=0;unkeyed_values_not_read=0;unrelated_values_not_read=0}
function Read-ReasonDictionary([long]$rva,[string]$module){
    if(-not $rva){return};$count=U32 $rva;if($count -gt 4096){throw 'Unbounded annotation dictionary.'};Bounds ($rva+4) ($count*8)
    for($i=0;$i -lt $count;$i++){
        $offset=$rva+4+$i*8;$key=Read-StringRva (U32 $offset);$annotationStats.dictionary_keys_examined++
        if($key -match $reasonKeys){$annotations.Add([ordered]@{module=$module;name=$key;value=(Read-StringRva (U32 ($offset+4)));kind='structured_simple_annotation'})}
        else{$annotationStats.unrelated_values_not_read++}
    }
}
try{
    if((U32 0) -ne 0x504d444d){throw 'Invalid MDMP signature.'}
    $version=U32 4;$count=U32 8;$directory=U32 12
    if(($version-band 0xffff) -ne 0xa793 -or $count -gt 128){throw 'Unexpected minidump version/directory count.'}
    Bounds $directory ($count*12)
    $streams=@{}
    for($i=0;$i -lt $count;$i++){$at=$directory+$i*12;$type=U32 $at;$size=U32 ($at+4);$rva=U32 ($at+8);Bounds $rva $size;$streams[[uint32]$type]=[pscustomobject]@{size=$size;rva=$rva}}
    $ex=$streams[[uint32]6];if(-not $ex -or $ex.size -lt 168){throw 'Missing bounded exception stream.'}
    $ea=[long]$ex.rva;$exception=[ordered]@{thread_id=(U32 $ea);code=(Hex32 (U32 ($ea+8)));flags=(Hex32 (U32 ($ea+12)));address=(Hex64 (U64 ($ea+24)));parameter_count=(U32 ($ea+32));information=@()}
    if($exception.parameter_count -gt 15){throw 'Invalid exception parameter count.'}
    for($i=0;$i -lt $exception.parameter_count;$i++){$exception.information+=Hex64 (U64 ($ea+40+$i*8))}
    $exceptionAddress=U64 ($ea+24)
    $ml=$streams[[uint32]4];if(-not $ml){throw 'Missing module list.'};$moduleCount=U32 $ml.rva
    if($moduleCount -gt 4096 -or $ml.size -lt 4+108*$moduleCount){throw 'Invalid module list.'}
    $modules=[Collections.Generic.List[object]]::new();$hitModule=$null
    for($i=0;$i -lt $moduleCount;$i++){
        $at=[long]$ml.rva+4+108*$i;$base=U64 $at;$size=U32 ($at+8);$name=Read-StringRva (U32 ($at+20)) $true
        $fvms=U32 ($at+32);$fvls=U32 ($at+36)
        $item=[ordered]@{index=$i;name=[IO.Path]::GetFileName($name);base=(Hex64 $base);image_size=$size;version=('{0}.{1}.{2}.{3}' -f ($fvms-shr 16),($fvms-band 65535),($fvls-shr 16),($fvls-band 65535))}
        $modules.Add($item)
        if($exceptionAddress -ge $base -and $exceptionAddress-$base -lt $size){$hitModule=[ordered]@{name=$item.name;path=$name;base=$item.base;image_size=$size;version=$item.version;exception_rva=(Hex64 ($exceptionAddress-$base))}}
    }
    $cp=$streams[[uint32]0x43500001]
    if($cp -and $cp.size -ge 52 -and (U32 $cp.rva) -ge 1){
        Read-ReasonDictionary (U32 ($cp.rva+40)) '<process>'
        $linkRva=U32 ($cp.rva+48)
        if($linkRva){
            $links=U32 $linkRva;if($links -gt $moduleCount){throw 'Invalid Crashpad module links.'};Bounds ($linkRva+4) ($links*12)
            for($i=0;$i -lt $links;$i++){
                $at=$linkRva+4+12*$i;$idx=U32 $at;$sz=U32 ($at+4);$info=U32 ($at+8)
                if($idx -ge $moduleCount -or $sz -lt 28){throw 'Invalid Crashpad module info.'};Bounds $info $sz
                if((U32 $info) -lt 1){continue};$module=$modules[$idx].name
                $listRva=U32 ($info+8);if($listRva){$annotationStats.unkeyed_values_not_read+=U32 $listRva}
                Read-ReasonDictionary (U32 ($info+16)) $module
                $objects=U32 ($info+24)
                if($objects){
                    $num=U32 $objects;if($num -gt 4096){throw 'Unbounded typed annotations.'};Bounds ($objects+4) ($num*12)
                    for($k=0;$k -lt $num;$k++){
                        $entry=$objects+4+12*$k;$key=Read-StringRva (U32 $entry);$type=U16 ($entry+4);$annotationStats.typed_keys_examined++
                        if($key -match $reasonKeys -and $type -eq 1){$annotations.Add([ordered]@{module=$module;name=$key;value=(Read-StringRva (U32 ($entry+8)));kind='structured_typed_string_annotation'})}
                        else{$annotationStats.unrelated_values_not_read++}
                    }
                }
            }
        }
    }
    $summary=[ordered]@{
        schema_version=1;kind='bounded_owned_minidump_exception_module_reason_metadata';status='parsed';dump=$DumpPath;bytes=$file.Length
        minidump_timestamp_utc=[DateTimeOffset]::FromUnixTimeSeconds((U32 20)).ToString('o');dump_sha256=(Get-FileHash -LiteralPath $DumpPath).Hash.ToLowerInvariant()
        exception=$exception;exception_module=$hitModule;module_count=$moduleCount
        known_reason_annotations=@($annotations);annotation_scope=$annotationStats
        exception_meaning='Interpret separately with official exception definitions and exact Chromium crash annotations; no symbolic stack claim.'
        raw_memory_readout='not_performed';stack_unwind='not_performed';all_string_scan='not_performed';debugger='not_used';upload='none';GUI='not_run'
        sources=@('https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_exception_stream','https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_exception','https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_module','https://chromium.googlesource.com/crashpad/crashpad/+/main/minidump/minidump_extensions.h')
    }
    $summary|ConvertTo-Json -Depth 9|Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
    $summary|ConvertTo-Json -Depth 9
}finally{$reader.Dispose();$file.Dispose()}
