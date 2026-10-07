#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TypeName,
    [Parameter(Mandatory)][string]$MethodName,
    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure'
)
# Read metadata/IL only. Never load Assembly-CSharp into an execution context.
$ErrorActionPreference = 'Stop'
$stream = [IO.File]::OpenRead((Join-Path $GamePath 'Wrath_Data\Managed\Assembly-CSharp.dll'))
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    function Resolve-Operand([int]$token) {
        $handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::EntityHandle($token)
        switch ($handle.Kind.ToString()) {
            'MethodDefinition' { return $metadata.GetString($metadata.GetMethodDefinition($handle).Name) }
            'FieldDefinition' { return $metadata.GetString($metadata.GetFieldDefinition($handle).Name) }
            'MemberReference' { return $metadata.GetString($metadata.GetMemberReference($handle).Name) }
            'TypeReference' { return $metadata.GetString($metadata.GetTypeReference($handle).Name) }
            'TypeDefinition' { return $metadata.GetString($metadata.GetTypeDefinition($handle).Name) }
            'MethodSpecification' { return Resolve-Operand ([System.Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($metadata.GetMethodSpecification($handle).Method)) }
            default { return $handle.Kind.ToString() }
        }
    }
    $opcodes = @{}
    foreach ($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
        $opcode = $field.GetValue($null)
        $opcodes[([int]$opcode.Value -band 65535)] = $opcode
    }
    $found = $false
    foreach ($typeHandle in $metadata.TypeDefinitions) {
        $type = $metadata.GetTypeDefinition($typeHandle)
        $fullName = $metadata.GetString($type.Namespace) + '.' + $metadata.GetString($type.Name)
        if ($fullName -ne $TypeName) { continue }
        foreach ($methodHandle in $type.GetMethods()) {
            $method = $metadata.GetMethodDefinition($methodHandle)
            if ($metadata.GetString($method.Name) -ne $MethodName) { continue }
            $found = $true
            if ($method.RelativeVirtualAddress -eq 0) { throw 'Méthode sans corps IL.' }
            $bytes = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $method.RelativeVirtualAddress).GetILBytes()
            Write-Output "$fullName.$MethodName"
            $index = 0
            while ($index -lt $bytes.Length) {
                $offset = $index
                $code = [int]$bytes[$index++]
                if ($code -eq 254) { $code = 65024 + $bytes[$index++] }
                $opcode = $opcodes[$code]
                if ($null -eq $opcode) { throw "Opcode inconnu : $code" }
                $operand = ''
                $length = 0
                switch ($opcode.OperandType.ToString()) {
                    'InlineNone' { }
                    { $_ -in @('ShortInlineI', 'ShortInlineVar', 'ShortInlineBrTarget') } { $length = 1; $operand = $bytes[$index] }
                    'InlineVar' { $length = 2; $operand = [BitConverter]::ToUInt16($bytes, $index) }
                    'InlineI8' { $length = 8; $operand = [BitConverter]::ToInt64($bytes, $index) }
                    'InlineR' { $length = 8; $operand = [BitConverter]::ToDouble($bytes, $index) }
                    'ShortInlineR' { $length = 4; $operand = [BitConverter]::ToSingle($bytes, $index) }
                    'InlineSwitch' { $count = [BitConverter]::ToInt32($bytes, $index); $length = 4 + 4 * $count; $operand = "targets=$count" }
                    default {
                        $length = 4
                        $operand = [BitConverter]::ToInt32($bytes, $index)
                        if ($opcode.OperandType.ToString() -in @('InlineMethod','InlineField','InlineType','InlineTok')) {
                            $operand = Resolve-Operand $operand
                        }
                    }
                }
                Write-Output ('IL_{0:X4}: {1} {2}' -f $offset, $opcode.Name, $operand)
                $index += $length
            }
        }
    }
    if (-not $found) { throw "Méthode introuvable : $TypeName.$MethodName" }
} finally { $stream.Dispose() }
