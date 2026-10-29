#Requires -Version 7.0
# Metadata only: never execute or load the mod/game into an execution context.
function Get-RtwpPackageProtocolVersion([string]$AssemblyPath) {
    $stream = [IO.File]::OpenRead($AssemblyPath)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($typeHandle in $metadata.TypeDefinitions) {
                $type = $metadata.GetTypeDefinition($typeHandle)
                if ($metadata.GetString($type.Namespace) -ne 'WOTRMultiplayer.Entities.Combat.RealTime' -or
                    $metadata.GetString($type.Name) -ne 'NetworkRealTimeCombatState') { continue }
                foreach ($fieldHandle in $type.GetFields()) {
                    $field = $metadata.GetFieldDefinition($fieldHandle)
                    if ($metadata.GetString($field.Name) -ne 'ProtocolVersion') { continue }
                    $valueHandle = $field.GetDefaultValue()
                    if ($valueHandle.IsNil -or -not ($field.Attributes -band [Reflection.FieldAttributes]::Literal)) {
                        throw 'Le protocole du paquet ne possède pas de constante vérifiable.'
                    }
                    $constant = $metadata.GetConstant($valueHandle)
                    if ($constant.TypeCode -ne [System.Reflection.Metadata.ConstantTypeCode]::Int32) {
                        throw 'Type de protocole du paquet invalide.'
                    }
                    $bytes = $metadata.GetBlobBytes($constant.Value)
                    if ($bytes.Length -ne 4) { throw 'Constante de protocole du paquet invalide.' }
                    $version = [BitConverter]::ToInt32($bytes, 0)
                    if ($version -lt 1) { throw 'Version de protocole du paquet invalide.' }
                    return $version
                }
            }
            throw 'Protocole RTwP absent du paquet : opération refusée.'
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-RtwpPackageManifestProtocol([string]$AssemblyPath, $Manifest) {
    $protocol = Get-RtwpPackageProtocolVersion $AssemblyPath
    if ($null -eq $Manifest -or $Manifest.protocolVersion -isnot [long] -and $Manifest.protocolVersion -isnot [int] -or
        $Manifest.protocolVersion -ne $protocol) {
        throw 'Le protocole du manifeste ne correspond pas à la DLL du candidat.'
    }
}
