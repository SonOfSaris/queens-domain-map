// Pretty-prints the blueprint bytecode in a `QDExport script` dump as pseudo-code.
// Usage: node kismet.mjs <dump.json> [functionNameFilter] [--grep=text]
import fs from 'node:fs';

const [file, ...rest] = process.argv.slice(2);
const grep = rest.find(a => a.startsWith('--grep='))?.slice(7).toLowerCase();
const filter = rest.find(a => !a.startsWith('--'))?.toLowerCase();
const dump = JSON.parse(fs.readFileSync(file, 'utf8'));

const objName = o => {
  if (!o) return 'None';
  if (typeof o === 'string') return o;
  const n = o.ObjectName || '';
  const m = /'(.*)'$/.exec(n);
  return m ? m[1] : n || JSON.stringify(o);
};
const varName = v => v?.Property?.Name ?? (v?.Path ? [].concat(v.Path).join('.') : v ? JSON.stringify(v).slice(0, 80) : '?');
const fn = f => (typeof f === 'string' ? f : objName(f).replace(/^.*:/, m => m)).replace(/^(Kismet\w+Library|Blueprint\w+Library|GameplayStatics):/, '');
const args = a => (a || []).map(x).join(', ');

function x(e) {
  if (e == null) return 'null';
  if (typeof e !== 'object') return JSON.stringify(e);
  switch (e.Token) {
    case 'EX_LocalVariable': case 'EX_LocalOutVariable': return varName(e.Variable);
    case 'EX_InstanceVariable': return 'this.' + varName(e.Variable);
    case 'EX_DefaultVariable': return 'default.' + varName(e.Variable);
    case 'EX_Let': case 'EX_LetBool': case 'EX_LetObj': case 'EX_LetWeakObjPtr': case 'EX_LetDelegate': case 'EX_LetMulticastDelegate':
      return `${x(e.Variable)} = ${x(e.Expression)}`;
    case 'EX_LetValueOnPersistentFrame': return `frame.${varName(e.DestinationProperty)} = ${x(e.AssignmentExpression)}`;
    case 'EX_Context': case 'EX_Context_FailSilent': case 'EX_ClassContext': return `${x(e.ObjectExpression)}.${x(e.ContextExpression)}`;
    case 'EX_InterfaceContext': return x(e.InterfaceValue);
    case 'EX_FinalFunction': case 'EX_LocalFinalFunction': case 'EX_CallMath':
    case 'EX_VirtualFunction': case 'EX_LocalVirtualFunction': return `${fn(e.Function)}(${args(e.Parameters)})`;
    case 'EX_CallMulticastDelegate': return `${x(e.Delegate)}.Broadcast[${fn(e.FunctionName)}](${args(e.Parameters)})`;
    case 'EX_Jump': return `goto ${e.CodeOffset}`;
    case 'EX_JumpIfNot': return `if (!(${x(e.BooleanExpression)})) goto ${e.CodeOffset}`;
    case 'EX_ComputedJump': return `goto [${x(e.CodeOffsetExpression)}]`;
    case 'EX_PushExecutionFlow': return `push ${e.PushingAddress}`;
    case 'EX_PopExecutionFlow': return 'pop';
    case 'EX_PopExecutionFlowIfNot': return `if (!(${x(e.BooleanExpression)})) pop`;
    case 'EX_Return': return `return ${e.Expression?.Token === 'EX_Nothing' ? '' : x(e.Expression)}`;
    case 'EX_EndOfScript': return 'end';
    case 'EX_Nothing': return 'nothing';
    case 'EX_ObjectConst': return objName(e.Value);
    case 'EX_SoftObjectConst': return 'soft(' + x(e.Value) + ')';
    case 'EX_IntConst': case 'EX_FloatConst': case 'EX_ByteConst': case 'EX_IntConstByte': case 'EX_Int64Const': case 'EX_UInt64Const': case 'EX_DoubleConst':
    case 'EX_SkipOffsetConst': return String(e.Value);
    case 'EX_StringConst': case 'EX_UnicodeStringConst': return JSON.stringify(e.Value);
    case 'EX_NameConst': return `'${e.Value}'`;
    case 'EX_TextConst': {
      const v = e.Value;
      const s = v?.SourceString ?? v?.LocalizedString ?? v?.KeyString ?? v;
      return 'text(' + (typeof s === 'object' ? x(s) + (v?.TableId ? '@' + x(v.TableId) : '') : JSON.stringify(s)) + (v?.KeyString && typeof v.KeyString === 'object' ? ' key=' + x(v.KeyString) : '') + ')';
    }
    case 'EX_True': return 'true';
    case 'EX_False': return 'false';
    case 'EX_NoObject': case 'EX_NoInterface': return 'None';
    case 'EX_Self': return 'self';
    case 'EX_IntZero': return '0';
    case 'EX_IntOne': return '1';
    case 'EX_StructMemberContext': return `${x(e.StructExpression)}.${varName({ Property: e.Property?.Property ?? e.Property, Path: e.Property?.Path })}`;
    case 'EX_SwitchValue': return `switch (${x(e.IndexTerm)}) { ${(e.Cases || []).map(c => `${x(c.CaseIndexValueTerm)}: ${x(c.CaseTerm)}`).join('; ')}; default: ${x(e.DefaultTerm)} }`;
    case 'EX_ArrayConst': return `[${args(e.Values)}]`;
    case 'EX_SetArray': return `${x(e.AssigningProperty)} = [${args(e.Elements)}]`;
    case 'EX_SetSet': return `${x(e.SetProperty)} = set[${args(e.Elements)}]`;
    case 'EX_SetConst': return `set[${args(e.Elements)}]`;
    case 'EX_SetMap': return `${x(e.MapProperty)} = map{${(e.Values || []).map(v => x(v.Key) + ': ' + x(v.Value)).join(', ')}}`;
    case 'EX_MapConst': return `map{${(e.Values || []).map(v => x(v.Key) + ': ' + x(v.Value)).join(', ')}}`;
    case 'EX_StructConst': return `${objName(e.Struct)}{${Object.entries(e.Properties || {}).map(([k, v]) => k + ': ' + x(v)).join(', ')}}`;
    case 'EX_DynamicCast': case 'EX_MetaCast': case 'EX_ObjToInterfaceCast': case 'EX_CrossInterfaceCast': case 'EX_InterfaceToObjCast':
      return `cast<${objName(e.InterfaceClass ?? e.Class ?? e.ClassPtr)}>(${x(e.Target)})`;
    case 'EX_Cast': case 'EX_PrimitiveCast': return `(${e.ConversionType})${x(e.Target)}`;
    case 'EX_BindDelegate': return `${x(e.Delegate)}.bind(${x(e.ObjectTerm)}, '${e.FunctionName}')`;
    case 'EX_AddMulticastDelegate': return `${x(e.MulticastDelegate)} += ${x(e.Delegate)}`;
    case 'EX_RemoveMulticastDelegate': return `${x(e.MulticastDelegate)} -= ${x(e.Delegate)}`;
    case 'EX_ClearMulticastDelegate': return `${x(e.DelegateToClear)}.clear()`;
    case 'EX_InstanceDelegate': return `delegate('${e.FunctionName}')`;
    case 'EX_ArrayGetByRef': return `${x(e.ArrayVariable)}[${x(e.ArrayIndex)}]`;
    case 'EX_VectorConst': case 'EX_RotationConst': case 'EX_TransformConst': case 'EX_Vector3fConst': return JSON.stringify(e.Value);
    case 'EX_Tracepoint': case 'EX_WireTracepoint': return '';
    default: {
      if (e.Token) return e.Token + JSON.stringify(e, (k, v) => (k === 'Token' || k === 'StatementIndex' ? undefined : v)).slice(0, 200);
      if (Array.isArray(e)) return '[' + e.map(x).join(', ') + ']';
      if (e.ObjectName) return objName(e);
      return JSON.stringify(e).slice(0, 200);
    }
  }
}

for (const f of dump) {
  if (!f.ScriptBytecode) continue;
  if (filter && !f.Name.toLowerCase().includes(filter)) continue;
  const lines = f.ScriptBytecode.map(s => [s.StatementIndex, x(s)]).filter(l => l[1]);
  if (grep && !lines.some(l => l[1].toLowerCase().includes(grep))) continue;
  console.log(`\n=== ${f.Name}`);
  for (const [i, s] of lines) {
    if (grep && !s.toLowerCase().includes(grep) && !/^(goto|if|push|pop|return|end)/.test(s)) { continue; }
    console.log(String(i).padStart(6) + '  ' + s);
  }
}
