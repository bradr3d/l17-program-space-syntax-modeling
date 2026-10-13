[xml]$doc = Get-Content -LiteralPath "C:\Users\Kevin\Documents\Senior Design\Week 1 Build\ExcelToBubbleDiagram.ghx" -Encoding UTF8
$ns = @{ gh = "" }
$objects = @()
$params = @{}
$PANEL_GUID = "59e0b89a-e487-49f8-bab8-b5bab16be14c"

function Get-ItemsDict($node) {
    $d = @{}
    $ids = @(); $sources = @()
    foreach ($item in $node.items.item) {
        $name = $item.name
        $text = if ($item.'#text') { $item.'#text'.Trim() } else { "" }
        if ($name -eq 'ID' -and $item.index -ne $null) { $ids += $text; continue }
        if ($name -eq 'Source') { $sources += $text; continue }
        if (-not $d.ContainsKey($name)) { $d[$name] = $text }
        else {
            $prev = $d[$name]
            if ($prev -isnot [System.Collections.IList]) { $d[$name] = @($prev) }
            $d[$name] += $text
        }
    }
    if ($ids.Count) { $d['_IDs'] = $ids }
    if ($sources.Count) { $d['_Sources'] = $sources }
    return $d
}

function Get-Persistent($node) {
    $pers = @()
    $pd = $node.chunks.chunk | Where-Object { $_.name -eq 'PersistentData' } | Select-Object -First 1
    if (-not $pd) { return $pers }
    foreach ($item in $pd.SelectNodes('.//item')) {
        $n = $item.name
        if ($n -in @('Count','PathCount','ItemCount')) { continue }
        $t = if ($item.'#text') { $item.'#text'.Trim() } else { "" }
        if ($t -and $pers -notcontains $t) { $pers += $t }
    }
    return $pers
}

$defObjects = $doc.SelectSingleNode("//chunk[@name='DefinitionObjects']")
foreach ($obj in $defObjects.chunks.chunk) {
    if ($obj.name -ne 'Object') { continue }
    $oi = Get-ItemsDict $obj
    $typeGuid = $oi['GUID']; $typeName = $oi['Name']
    $container = $obj.chunks.chunk | Where-Object { $_.name -eq 'Container' } | Select-Object -First 1
    if (-not $container) { continue }
    $ci = Get-ItemsDict $container
    $inst = $ci['InstanceGuid']; $nick = $ci['NickName']; $desc = $ci['Description']
    $rec = [ordered]@{ type_guid=$typeGuid; type_name=$typeName; instance_guid=$inst; nickname=$nick; description=$desc }
    $pp = $container.chunks.chunk | Where-Object { $_.name -eq 'PanelProperties' } | Select-Object -First 1
    if ($pp) {
        $ppd = Get-ItemsDict $pp
        $rec['panel_text'] = $ppd['UserText']
    }
    if ($typeName -eq 'Group') {
        $rec['member_ids'] = $ci['_IDs']
        $objects += [pscustomobject]$rec
        continue
    }
    $inputs = @(); $outputs = @()
    foreach ($pch in $container.chunks.chunk) {
        if ($pch.name -notlike 'param_*') { continue }
        $pi = Get-ItemsDict $pch
        $pInst = $pi['InstanceGuid']
        $pRec = @{
            instance_guid = $pInst
            nickname = $pi['NickName']
            kind = if ($pch.name -like '*input*') { 'input' } else { 'output' }
            sources = @($pi['_Sources'])
            persistent = @(Get-Persistent $pch)
        }
        $params[$pInst] = $pRec + @{ parent_instance=$inst; parent_nick=$nick; parent_type=$typeName; parent_type_guid=$typeGuid }
        if ($pRec.kind -eq 'input') { $inputs += $pRec } else { $outputs += $pRec }
    }
    $rec['inputs'] = $inputs; $rec['outputs'] = $outputs
    if ($typeGuid -eq $PANEL_GUID) {
        $pers = @(Get-Persistent $container)
        if ($rec['panel_text'] -and $pers -notcontains $rec['panel_text']) { $pers = @($rec['panel_text']) + $pers }
        $params[$inst] = @{ instance_guid=$inst; nickname=$nick; kind='panel'; sources=@($ci['_Sources']); persistent=$pers; parent_type='Panel'; parent_type_guid=$typeGuid }
    }
    $objects += [pscustomobject]$rec
}

$typeCounts = $objects | Group-Object { "$($_.type_name) | $($_.type_guid)" } | Sort-Object Count -Descending
Write-Output "OBJECT_COUNT=$($objects.Count)"
$wires = @()
foreach ($kv in $params.GetEnumerator()) {
    foreach ($s in $kv.Value.sources) { $wires += [pscustomobject]@{ Source=$s; Target=$kv.Key } }
}
Write-Output "WIRE_COUNT=$($wires.Count)"
Write-Output "`n=== TYPE COUNTS ==="
foreach ($g in $typeCounts) {
    Write-Output ("{0,3} {1}" -f $g.Count, $g.Name)
    $nicks = $g.Group | ForEach-Object { if ($_.nickname) { $_.nickname } elseif ($_.panel_text) { $_.panel_text.Substring(0,[Math]::Min(40,$_.panel_text.Length)) } } | Where-Object { $_ } | Select-Object -Unique
    if ($nicks) { Write-Output ("     nicks: " + ($nicks -join ' | ')) }
}
Write-Output "`n=== GROUPS ==="
$objects | Where-Object type_name -eq 'Group' | ForEach-Object { Write-Output "members=$($_.member_ids.Count) inst=$($_.instance_guid)" }
Write-Output "`n=== UNIQUE COMPONENTS (non-empty nick or panel) ==="
$seen = @{}
foreach ($o in ($objects | Where-Object { $_.type_name -ne 'Group' })) {
    $k = "$($o.type_guid)|$($o.nickname)|$($o.panel_text)"
    if ($seen[$k]) { continue }; $seen[$k] = $true
    if (-not $o.nickname -and -not $o.panel_text) { continue }
    Write-Output "$($o.type_name) | $($o.nickname) | $($o.type_guid) | $($o.instance_guid)"
}
$objects | ConvertTo-Json -Depth 8 | Out-File "C:\Users\Kevin\Documents\Senior Design\Week 1 Build\_ghx_objects.json" -Encoding utf8
$wires | ConvertTo-Json | Out-File "C:\Users\Kevin\Documents\Senior Design\Week 1 Build\_ghx_wires.json" -Encoding utf8
