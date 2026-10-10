using System.Reflection;
using System.Runtime.Loader;
if(args.Length==0) throw new ArgumentException("Test assembly path required");
var path=Path.GetFullPath(args[0]);
var directory=Path.GetDirectoryName(path)!;
AssemblyLoadContext.Default.Resolving += (_, name) => {
 var candidate=Path.Combine(directory,name.Name+".dll");
 return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var filters=args.Skip(1).SelectMany(a=>a.Split('|')).ToArray();
int passed=0,failed=0;
foreach(var type in assembly.GetTypes().OrderBy(t=>t.FullName))
foreach(var method in type.GetMethods().OrderBy(m=>m.Name)) {
 var attrs=method.GetCustomAttributesData();
 var fact=attrs.FirstOrDefault(a=>a.AttributeType.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute");
 if(fact==null) continue;
 var name=type.Name+"."+method.Name;
 if(filters.Length>0&&!filters.Any(name.Contains)) continue;
 if(fact.NamedArguments.Any(a=>a.MemberName=="Skip"&&a.TypedValue.Value!=null)) { Console.WriteLine("UNSUPPORTED SKIP "+name); failed++; continue; }
 var rows=attrs.Where(a=>a.AttributeType.FullName=="Xunit.InlineDataAttribute").Select(a=>((IReadOnlyCollection<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).Select(v=>v.Value).ToArray()).ToArray();
 if(fact.AttributeType.FullName=="Xunit.TheoryAttribute"&&rows.Length==0) {Console.WriteLine("UNSUPPORTED THEORY "+name);failed++;continue;}
 if(rows.Length==0)rows=[Array.Empty<object?>()];
 foreach(var row in rows){
  object? instance=null;
  try{
   instance=method.IsStatic?null:Activator.CreateInstance(type);
   var result=method.Invoke(instance,row);
   if(result is Task task)await task;
   if(result is ValueTask vt)await vt;
   passed++; Console.WriteLine("PASS "+name);
  }catch(Exception ex){
   while(ex is TargetInvocationException&&ex.InnerException!=null)ex=ex.InnerException;
   failed++;Console.WriteLine("FAIL "+name+"\n"+ex);
  }finally{if(instance is IAsyncDisposable ad)await ad.DisposeAsync();else if(instance is IDisposable d)d.Dispose();}
 }
}
Console.WriteLine($"IN-PROCESS FACT CHECKS: {passed} passed, {failed} failed; no testhost or network listener used.");
return failed==0&&passed>0?0:1;
