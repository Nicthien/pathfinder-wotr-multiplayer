#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TypeName,
    [Parameter(Mandatory)][string]$MethodName,
    [switch]$FindCallers,
    [string]$CallerTypePattern = '*',
    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure'
)
# Read metadata/IL only. Never load Assembly-CSharp into an execution context.
$ErrorActionPreference = 'Stop'
$stream = [IO.File]::OpenRead((Join-Path $GamePath 'Wrath_Data\Managed\Assembly-CSharp.dll'))
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    function Resolve-TypeName([System.Reflection.Metadata.EntityHandle]$handle) {
        switch ($handle.Kind.ToString()) {
            'TypeDefinition' {
                $definition = $metadata.GetTypeDefinition($handle)
                $name = $metadata.GetString($definition.Name)
                $parent = $definition.GetDeclaringType()
                if (-not $parent.IsNil) { return (Resolve-TypeName $parent) + '+' + $name }
                $namespace = $metadata.GetString($definition.Namespace)
                return $(if ($namespace) { $namespace + '.' + $name } else { $name })
            }
            'TypeReference' {
                $reference = $metadata.GetTypeReference($handle)
                $name = $metadata.GetString($reference.Name)
                if ($reference.ResolutionScope.Kind.ToString() -eq 'TypeReference') {
                    return (Resolve-TypeName $reference.ResolutionScope) + '+' + $name
                }
                $namespace = $metadata.GetString($reference.Namespace)
                return $(if ($namespace) { $namespace + '.' + $name } else { $name })
            }
            # Generic signatures are deliberately not guessed from their member name.
            default { return '<' + $handle.Kind.ToString() + '>' }
        }
    }
    function Resolve-Operand([int]$token) {
        $handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::EntityHandle($token)
        switch ($handle.Kind.ToString()) {
            'MethodDefinition' {
                $definition = $metadata.GetMethodDefinition($handle)
                return (Resolve-TypeName $definition.GetDeclaringType()) + '::' + $metadata.GetString($definition.Name)
            }
            'FieldDefinition' {
                $definition = $metadata.GetFieldDefinition($handle)
                return (Resolve-TypeName $definition.GetDeclaringType()) + '::' + $metadata.GetString($definition.Name)
            }
            'MemberReference' {
                $reference = $metadata.GetMemberReference($handle)
                return (Resolve-TypeName $reference.Parent) + '::' + $metadata.GetString($reference.Name)
            }
            { $_ -in @('TypeReference', 'TypeDefinition') } { return Resolve-TypeName $handle }
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
        $fullName = Resolve-TypeName $typeHandle
        if ($FindCallers -and $fullName -notlike $CallerTypePattern) { continue }
        if (-not $FindCallers -and $fullName -ne $TypeName) { continue }
        foreach ($methodHandle in $type.GetMethods()) {
            $method = $metadata.GetMethodDefinition($methodHandle)
            if (-not $FindCallers -and $metadata.GetString($method.Name) -ne $MethodName) { continue }
            if ($FindCallers -and $method.RelativeVirtualAddress -eq 0) { continue }
            if (-not $FindCallers) { $found = $true }
            if ($method.RelativeVirtualAddress -eq 0) { throw 'Méthode sans corps IL.' }
            $bytes = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $method.RelativeVirtualAddress).GetILBytes()
            if (-not $FindCallers) { Write-Output "$fullName.$MethodName" }
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
                if (-not $FindCallers) {
                    Write-Output ('IL_{0:X4}: {1} {2}' -f $offset, $opcode.Name, $operand)
                } elseif ($opcode.OperandType.ToString() -eq 'InlineMethod' -and $operand -eq ($TypeName + '::' + $MethodName)) {
                    $found = $true
                    Write-Output ('{0}::{1} IL_{2:X4}' -f $fullName, $metadata.GetString($method.Name), $offset)
                }
                $index += $length
            }
        }
    }
    if (-not $found) { throw "Méthode introuvable : $TypeName.$MethodName" }
} finally { $stream.Dispose() }
