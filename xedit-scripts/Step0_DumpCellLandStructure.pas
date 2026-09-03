{
  Step0_DumpCellLandStructure.pas

  DIAGNOSTIC ONLY - makes no changes to any record.

  Run this on one or two exterior CELL records to sanity-check the raw-byte
  VHGT decoder before trusting FindLandscapeSeams.pas across your whole
  load order. It prints:
    - the cell's XCLC grid coordinates
    - whether the CELL -> LAND lookup (via ChildGroup) works
    - VHGT's raw byte array (confirmed to be a flat 1096-byte blob with no
      sub-elements - GetNativeValue on it returns a Variant byte array)
    - the decoded Offset float and the four corner heights of the cell

  Sanity check: the printed Offset should be a plausible Skyrim height
  (roughly -3000 to +30000), and "SW corner" should equal Offset exactly.
  If either looks wrong (huge, tiny, NaN), something in the byte layout
  assumption is off and DecodeAndPrint needs fixing before the main scan
  is trustworthy.

  HOW TO RUN
  1. Copy this file into your xEdit "Edit Scripts" folder.
  2. Open xEdit with your normal load order (or just Skyrim.esm/Update.esm
     plus one worldspace mod you know edits landscape).
  3. In the left tree, drill into a worldspace, e.g.
     Skyrim.esm -> Worldspace -> Tamriel -> Block -> Sub-Block, and pick
     any single CELL record that has landscape data (an ordinary exterior
     cell).
  4. Right-click that ONE record -> Apply Script... -> select this script
     -> OK.
  5. Open View -> Message Log (or the Messages tab) and copy everything
     it printed back to me.
}
unit UserScript;

interface

implementation

function TryFindLandInContainer(container: IInterface): IInterface;
var
  i: integer;
  rec: IInterface;
begin
  Result := nil;
  if not Assigned(container) then exit;
  for i := 0 to ElementCount(container) - 1 do begin
    rec := ElementByIndex(container, i);
    if Signature(rec) = 'LAND' then begin
      Result := rec;
      exit;
    end;
  end;
end;

function GetLandFromCell(cellRecord: IInterface): IInterface;
var
  grp, sub: IInterface;
  i: integer;
begin
  Result := nil;
  grp := ChildGroup(cellRecord);
  if not Assigned(grp) then begin
    AddMessage('  ChildGroup(cell) returned nothing.');
    exit;
  end;

  // LAND usually sits one level deeper, inside the cell's "Temporary" subgroup
  Result := TryFindLandInContainer(grp);
  if Assigned(Result) then exit;

  for i := 0 to ElementCount(grp) - 1 do begin
    sub := ElementByIndex(grp, i);
    Result := TryFindLandInContainer(sub);
    if Assigned(Result) then exit;
  end;
end;

procedure DumpTree(e: IInterface; prefix: string; depth: integer);
var
  i, n, lo, hi: integer;
  nm, vs, sample: string;
  v: variant;
begin
  if not Assigned(e) then exit;
  if depth > 4 then exit;

  n := ElementCount(e);
  nm := Name(e);
  vs := '';
  sample := '';

  if n = 0 then begin
    v := GetNativeValue(e);
    if VarIsArray(v) then begin
      lo := VarArrayLowBound(v, 1);
      hi := VarArrayHighBound(v, 1);
      vs := ' = <array, len=' + IntToStr(hi - lo + 1) + '>';
      for i := lo to lo + 15 do begin
        if i > hi then break;
        sample := sample + IntToStr(integer(v[i])) + ' ';
      end;
    end
    else if VarIsStr(v) or VarIsNumeric(v) then
      vs := ' = ' + VarToStr(v)
    else
      vs := ' = <vartype=' + IntToStr(VarType(v)) + '>';
  end;

  AddMessage(prefix + nm + '  count=' + IntToStr(n) + vs);
  if sample <> '' then
    AddMessage(prefix + '  first bytes: ' + sample);

  for i := 0 to n - 1 do
    DumpTree(ElementByIndex(e, i), prefix + '  ', depth + 1);
end;

function Pow2(n: integer): double;
var
  i: integer;
  r: double;
begin
  r := 1.0;
  if n >= 0 then begin
    for i := 1 to n do r := r * 2.0;
  end
  else begin
    for i := 1 to -n do r := r / 2.0;
  end;
  Result := r;
end;

// Extracts sign/exponent/mantissa straight from the bytes (no 32-bit
// assembly step) so every intermediate value stays well under 2^31 and
// no Int64 is needed.
function BytesToSingleLE(b0, b1, b2, b3: integer): double;
var
  sign, exponent, mantissa: integer;
begin
  if (b3 and $80) <> 0 then sign := -1 else sign := 1;
  exponent := ((b3 and $7F) shl 1) or ((b2 and $80) shr 7);
  mantissa := ((b2 and $7F) shl 16) or (b1 shl 8) or b0;
  if exponent = 0 then
    Result := sign * (mantissa / 8388608.0) * Pow2(-126)
  else
    Result := sign * (1.0 + mantissa / 8388608.0) * Pow2(exponent - 127);
end;

function SignedByte(b: integer): integer;
begin
  if b > 127 then
    Result := b - 256
  else
    Result := b;
end;

procedure DecodeAndPrint(vhgt: IInterface);
var
  v: variant;
  lo: integer;
  vOffset: double;
  row, col, base: integer;
  delta: integer;
  heights: array[0..32, 0..32] of double;
begin
  v := GetNativeValue(vhgt);
  lo := VarArrayLowBound(v, 1);

  vOffset := BytesToSingleLE(
    integer(v[lo + 0]), integer(v[lo + 1]),
    integer(v[lo + 2]), integer(v[lo + 3]));
  AddMessage('  Offset = ' + FloatToStr(vOffset));

  base := lo + 4;
  for row := 0 to 32 do
    for col := 0 to 32 do begin
      delta := SignedByte(integer(v[base + row * 33 + col]));
      if col = 0 then begin
        if row = 0 then
          heights[0][0] := vOffset + delta * 8.0
        else
          heights[row][0] := heights[row - 1][0] + delta * 8.0;
      end
      else
        heights[row][col] := heights[row][col - 1] + delta * 8.0;
    end;

  AddMessage('  Absolute height at SW corner (0,0)   = ' + FloatToStr(heights[0][0]));
  AddMessage('  Absolute height at SE corner (0,32)  = ' + FloatToStr(heights[0][32]));
  AddMessage('  Absolute height at NW corner (32,0)  = ' + FloatToStr(heights[32][0]));
  AddMessage('  Absolute height at NE corner (32,32) = ' + FloatToStr(heights[32][32]));
  AddMessage('  (SW corner should equal Offset exactly)');
end;

function Process(e: IInterface): integer;
var
  cell, land, winLand, vhgt, xclc: IInterface;
  x, y, lo, hi, i, cnt, b: integer;
  v: variant;
  vOffset: double;
begin
  Result := 0;

  if Signature(e) <> 'CELL' then begin
    AddMessage('Please select a single CELL record, not: ' + Signature(e));
    exit;
  end;

  cell := WinningOverride(e);
  AddMessage('=== Cell: ' + Name(cell) + ' ===');

  xclc := ElementByPath(cell, 'XCLC');
  if Assigned(xclc) then begin
    x := GetElementNativeValues(cell, 'XCLC\X');
    y := GetElementNativeValues(cell, 'XCLC\Y');
    AddMessage('XCLC coords: X=' + IntToStr(x) + ' Y=' + IntToStr(y));
  end
  else
    AddMessage('No XCLC on this cell - it is probably interior. Pick an exterior cell instead.');

  land := GetLandFromCell(e);
  if not Assigned(land) then begin
    AddMessage('No LAND record found under this cell via ChildGroup. Pick a different cell.');
    exit;
  end;

  winLand := WinningOverride(land);
  AddMessage('Found LAND record, winning override plugin: ' + GetFileName(GetFile(winLand)));

  vhgt := ElementBySignature(winLand, 'VHGT');
  if not Assigned(vhgt) then begin
    AddMessage('LAND record has no VHGT subrecord (unusual - check this cell is a normal landscaped cell).');
    exit;
  end;

  AddMessage('MINIMAL TEST: one GetNativeValue call, reading only 4 bytes.');
  v := GetNativeValue(vhgt);
  AddMessage('  Got variant OK.');
  lo := VarArrayLowBound(v, 1);
  AddMessage('  LowBound = ' + IntToStr(lo));
  hi := VarArrayHighBound(v, 1);
  AddMessage('  HighBound = ' + IntToStr(hi));

  vOffset := BytesToSingleLE(
    integer(v[lo + 0]), integer(v[lo + 1]),
    integer(v[lo + 2]), integer(v[lo + 3]));
  AddMessage('  Offset (bytes ' + IntToStr(lo) + '..' + IntToStr(lo + 3) + ') = ' + FloatToStr(vOffset));
  AddMessage('MINIMAL TEST DONE - if you see this line, the crash is NOT in basic 4-byte reads.');

  AddMessage('');
  AddMessage('FULL SEQUENTIAL READ TEST - reading every byte ' + IntToStr(lo) + '..' + IntToStr(hi) +
    ', checkpoint every 50. If this aborts or crashes, note the LAST checkpoint number printed.');
  cnt := 0;
  for i := lo to hi do begin
    b := integer(v[i]);
    Inc(cnt);
    if (cnt mod 50) = 0 then
      AddMessage('  checkpoint: read ' + IntToStr(cnt) + ' bytes OK (index ' + IntToStr(i) + ', value ' + IntToStr(b) + ')');
  end;
  AddMessage('FULL READ TEST DONE - read all ' + IntToStr(cnt) + ' bytes with no crash.');
end;

end.
