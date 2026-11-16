using System.Net;
using System.Text;
using System.Text.Json;
using BimFamilyAI.Core;
using BimFamilyAI.Meshy;

var fixtures = Path.GetFullPath(args.Length > 0 ? args[0] : "tests/fixtures");
var obj = await File.ReadAllBytesAsync(Path.Combine(fixtures, "cube_500mm.obj"));
int passed = 0;
void Check(bool condition, string name)
{ if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); passed++; }
async Task<MeshyException> Reject(Func<Task> action, MeshyError code, string name)
{
    try { await action(); } catch (MeshyException ex) { Check(ex.Code == code, name + " (" + ex.Code + ")"); return ex; }
    throw new Exception("Accepted invalid case: " + name);
}
HttpResponseMessage FileResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
var oldKey = Environment.GetEnvironmentVariable("MESHY_API_KEY");
Environment.SetEnvironmentVariable("MESHY_API_KEY", null);
try
{
    using (var s = new Scenario())
    {
        // A: a real, locally constructed 32x32 PNG, but all HTTP is in memory.
        s.Api.Steps.Enqueue(async (r, _) =>
        {
            Check(r.RequestUri!.AbsoluteUri == "https://api.meshy.ai/openapi/v1/image-to-3d" && r.Method == HttpMethod.Post, "A endpoint");
            Check(r.Headers.Authorization?.ToString() == "Bearer simulation-only", "A simulated Bearer, no API key");
            using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()); var p = json.RootElement;
            Check(p.EnumerateObject().Count() == 6 && p.GetProperty("model_type").GetString() == "smart-topology" &&
                p.GetProperty("ai_model").GetString() == "meshy-t2" && !p.GetProperty("should_texture").GetBoolean() &&
                p.GetProperty("target_polycount").GetInt32() == 4000 && p.GetProperty("target_formats")[0].GetString() == "obj", "A exact supported JSON profile");
            Check(p.GetProperty("image_url").GetString() == "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(s.Image)), "A Data URI");
            return FakeHandler.Json(new { result = s.Id },202);
        });
        var record = await s.Client.CreateAsync(new(s.Image));
        Check(record.TaskId == s.Id && record.Submission == SubmissionState.Known, "A accepted ID");
        Check(record.AttemptedAt is not null && record.CreatedAt is null,"A attempt date distinguished from unknown server creation date");
        var persisted = (await new JsonTaskStore(dataDirectory:s.DirectoryPath).ReadAsync()).Single();
        Check(persisted.TaskId == s.Id && persisted.Options?.TargetPolycount == 4000 && persisted.ImageHash?.Length == 64, "A persistent identity and profile");
        var again = await s.Client.CreateAsync(new(s.Image));
        Check(again.TaskId == s.Id && s.Api.Posts == 1, "A duplicate uses previous ID");
        await Reject(() => s.Client.CreateAsync(new(s.Image), new(15001)), MeshyError.InvalidInput, "A polycount validation before HTTP");

        // B: bounded GET retry/backoff and server hint on both success and throttling.
        s.Api.Add(_ => FakeHandler.State(s.Id,"PENDING",0,retry:7));
        s.Api.Add(_ => FakeHandler.Json(new { message="not logged" },429,11));
        s.Api.Add(_ => FakeHandler.State(s.Id,"IN_PROGRESS",45));
        s.Api.Add(_ => FakeHandler.Json(new { message="not logged" },500));
        s.Api.Add(_ => FakeHandler.State(s.Id));
        var events = new List<GenerationProgress>();
        var done = await s.Client.TrackAsync(s.Id,new InlineProgress(events.Add));
        Check(events.Select(x=>x.State).SequenceEqual(new[] {ProcessingState.Pending,ProcessingState.InProgress,ProcessingState.Succeeded}) &&
            events.Select(x=>x.Percent).SequenceEqual(new int?[] {0,45,100}), "B states and progress");
        Check(s.Waits.Select(x=>x.TotalSeconds).SequenceEqual(new double[] {7,11,5,5}), "B Retry-After and backoff");
        Check(done.State == ProcessingState.Succeeded && (await s.Store.ReadAsync()).Single().Progress == 100, "B terminal state persisted");
        Check((await s.Store.ReadAsync()).Single().CreatedAt==DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L),"B official server creation timestamp");

        // C, G, H: streamed OBJ, shared inspection, explicit units, offline reuse after restart.
        s.Api.Add(_ => FakeHandler.State(s.Id));
        s.Files.Add(r => { Check(r.Headers.Authorization == null && r.RequestUri!.Host == "assets.meshy.ai", "C no Bearer on storage"); return FileResponse(obj); });
        var result = await s.Client.GetResultAsync(s.Id);
        Check(File.Exists(result.LocalPath) && result.Bounds == new ObjBounds(500,500,500), "C downloaded OBJ inspected");
        var import = result.ToImportRequest(ObjUnit.Millimeters,ObjVerticalAxis.Z,500,"Meshy_test","Simulación");
        Check(import.Path == result.LocalPath && import.TargetHeightMm == 500 && import.Unit == ObjUnit.Millimeters, "C ObjImportRequest compatibility");
        var offlineApi = new FakeHandler(); var offlineFiles = new FakeHandler();
        using var restarted = MeshyClient.CreateSimulated(offlineApi,offlineFiles,new(dataDirectory:s.DirectoryPath));
        var reused = await restarted.GetResultAsync(s.Id);
        Check(reused.LocalPath == result.LocalPath && offlineApi.Calls == 0 && offlineFiles.Calls == 0, "G valid local cache works offline after restart");
        string journal = await File.ReadAllTextAsync(Path.Combine(s.Store.Root,"tasks.json"));
        Check(!journal.Contains("base64") && !journal.Contains("data:image") && !journal.Contains("https:") &&
            !journal.Contains("Signature") && !journal.Contains("simulation-only") && !journal.Contains("Authorization"), "H no credentials, image data or signed URLs in journal");
    }

    // D: creation is never retried, even for 429/500; unsafe server messages never escape.
    foreach (var (status, code) in new[] { (400,MeshyError.BadRequest),(401,MeshyError.InvalidCredential),(402,MeshyError.NoCredits),
        (403,MeshyError.Forbidden),(404,MeshyError.NotFound),(429,MeshyError.RateLimited),(500,MeshyError.ServerError) })
    {
        using var s = new Scenario();
        const string secret = "DO-NOT-LOG-fake-secret";
        s.Api.Add(_=>FakeHandler.Json(new { message=secret+" data:image/png;base64,AAAA https://assets.meshy.ai/?Signature=secret" },status,13));
        var ex = await Reject(()=>s.Client.CreateAsync(new(s.Image)),code,"D HTTP " + status);
        Check(ex.HttpStatus == status && ex.RetryAfter == TimeSpan.FromSeconds(13) && !ex.ToString().Contains(secret) &&
            !(await File.ReadAllTextAsync(Path.Combine(s.Store.Root,"tasks.json"))).Contains(secret) && s.Api.Posts == 1 && s.Waits.Count == 0, "D sanitized, no POST retry " + status);
        await Reject(()=>s.Client.CreateAsync(new(s.Image)),status==500?MeshyError.CreationUncertain:MeshyError.Duplicate,"D rejected/uncertain attempt cannot repeat " + status);
    }

    // E: local cancellation retains the ID; a new client resumes with GET alone.
    using (var cancellation = new CancellationTokenSource())
    using (var s = new Scenario((_, token)=> { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }))
    {
        s.CreateResponse(); await s.Client.CreateAsync(new(s.Image));
        s.Api.Add(_=>FakeHandler.State(s.Id,"PENDING",0));
        try { await s.Client.TrackAsync(s.Id,cancellationToken:cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true,"E local tracking canceled"); }
        var api = new FakeHandler(); var files = new FakeHandler(); api.Add(_=>FakeHandler.State(s.Id));
        using var restarted = MeshyClient.CreateSimulated(api,files,new(dataDirectory:s.DirectoryPath));
        await restarted.TrackAsync(s.Id);
        Check(s.Api.Posts == 1 && api.Posts == 0 && api.Gets == 1 && (await s.Store.ReadAsync()).Single().TaskId == s.Id,"E resume known ID, no second POST");
    }

    // F: invalid outputs are never returned, partial files removed, prior OBJ untouched.
    foreach (var name in new[] {"empty","html","interrupted","size-header","size-stream","length-mismatch","untrusted","redirect","http","userinfo","port","lookalike"})
    {
        using var s = new Scenario(); await s.Client.RecoverAsync(s.Id);
        string? url = name switch {
            "untrusted"=>"https://evil.example/model.obj", "http"=>"http://assets.meshy.ai/model.obj",
            "userinfo"=>"https://secret@assets.meshy.ai/model.obj", "port"=>"https://assets.meshy.ai:444/model.obj",
            "lookalike"=>"https://assets.meshy.ai.evil.example/model.obj", _=>"https://assets.meshy.ai/model.obj" };
        s.Api.Add(_=>FakeHandler.State(s.Id,url:url));
        MeshyError expected = name switch {
            "empty" or "html"=>MeshyError.InvalidObj, "interrupted" or "length-mismatch"=>MeshyError.IncompleteDownload,
            "size-header" or "size-stream"=>MeshyError.DownloadTooLarge, _=>MeshyError.UntrustedUrl };
        if (name is "empty" or "html") s.Files.Add(_=>FileResponse(name=="empty"?[]:Encoding.UTF8.GetBytes("<html>not an OBJ</html>")));
        if (name=="interrupted") s.Files.Add(_=>new(HttpStatusCode.OK) {Content=new StreamContent(new BrokenStream())});
        if (name=="size-header") s.Files.Add(_=> { var r=FileResponse(obj);r.Content.Headers.ContentLength=ObjInspection.MaxBytes+1;return r; });
        if (name=="size-stream") s.Files.Add(_=>new(HttpStatusCode.OK) {Content=new StreamContent(new OversizeStream())});
        if (name=="length-mismatch") s.Files.Add(_=> {var r=FileResponse(obj);r.Content.Headers.ContentLength=obj.Length+1;return r;});
        if (name=="redirect") s.Files.Add(_=> {var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new("https://evil.example/model.obj");return r;});
        await Reject(()=>s.Client.GetResultAsync(s.Id),expected,"F " + name);
        var stored=(await s.Store.ReadAsync()).Single();
        Check(stored.Download == DownloadState.Failed && stored.ObjPath == null && !Directory.EnumerateFiles(s.Store.Root,"*.obj").Any(),"F no invalid or partial artifact " + name);
    }

    // Expiry refresh is bounded and never creates a replacement generation.
    using (var s = new Scenario())
    {
        for(int i=0;i<2;i++) {s.Api.Add(_=>FakeHandler.State(s.Id));s.Files.Add(_=>new(HttpStatusCode.Forbidden));}
        await Reject(()=>s.Client.GetResultAsync(s.Id),MeshyError.ExpiredUrl,"expired URL after one refresh");
        Check(s.Api.Gets == 2 && s.Api.Posts == 0 && s.Files.Gets == 2,"expiry never regenerates");
    }
    using (var s = new Scenario())
    {
        s.Api.Add(_=>FakeHandler.State(s.Id)); s.Files.Add(_=>new(HttpStatusCode.Forbidden));
        s.Api.Add(_=>FakeHandler.State(s.Id)); s.Files.Add(_=>FileResponse(obj));
        Check(File.Exists((await s.Client.GetResultAsync(s.Id)).LocalPath) && s.Api.Posts == 0,"expiry refresh can recover available file");
    }

    // Ambiguous creation across restarts must not create another task.
    foreach (var name in new[] {"disconnect","timeout","invalid-json","invalid-id","canceled"})
    {
        using var s = new Scenario(); using var cts = new CancellationTokenSource();
        s.Api.Steps.Enqueue((_,_)=> name switch {
            "disconnect"=>Task.FromException<HttpResponseMessage>(new HttpRequestException("sensitive details")),
            "timeout"=>Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
            "canceled"=>Cancel(),
            "invalid-id"=>Task.FromResult(FakeHandler.Json(new {result="../../unsafe"},202)),
            _=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) {Content=new StringContent("{bad")}) });
        Task<HttpResponseMessage> Cancel() {cts.Cancel();return Task.FromCanceled<HttpResponseMessage>(cts.Token);}
        if (name=="canceled")
        {
            try {await s.Client.CreateAsync(new(s.Image),cancellationToken:cts.Token);throw new Exception("Cancellation ignored");}
            catch(OperationCanceledException) {Check(true,"creation cancellation propagated");}
        }
        else await Reject(()=>s.Client.CreateAsync(new(s.Image)),name=="disconnect"?MeshyError.NetworkFailure:name=="timeout"?MeshyError.NetworkTimeout:MeshyError.InvalidJson,"ambiguous " + name);
        var api=new FakeHandler(); using var restarted=MeshyClient.CreateSimulated(api,new FakeHandler(),new(dataDirectory:s.DirectoryPath));
        await Reject(()=>restarted.CreateAsync(new(s.Image)),MeshyError.CreationUncertain,"ambiguous reservation survives restart " + name);
        Check(s.Api.Posts == 1 && api.Calls == 0,"one POST only " + name);
    }

    // Concurrent clients reserve atomically before POST.
    using (var s = new Scenario())
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Api.Steps.Enqueue(async (_,_)=> {entered.SetResult();await release.Task;return FakeHandler.Json(new {result=s.Id},202);});
        var first=s.Client.CreateAsync(new(s.Image)); await entered.Task;
        using var second=MeshyClient.CreateSimulated(new FakeHandler(),new FakeHandler(),new(dataDirectory:s.DirectoryPath));
        await Reject(()=>second.CreateAsync(new(s.Image)),MeshyError.CreationUncertain,"concurrent duplicate blocked");
        release.SetResult();await first;
        Check(s.Api.Posts == 1,"concurrent one POST");
    }

    // GET errors: bounded retry, unknown states, task failure, malformed JSON and missing OBJ.
    using (var s = new Scenario())
    {
        for(int i=0;i<3;i++) s.Api.Add(_=>FakeHandler.Json(new {message="hidden"},500));
        await Reject(()=>s.Client.QueryAsync(s.Id),MeshyError.ServerError,"GET retries exhausted");
        Check(s.Api.Gets==3 && s.Waits.SequenceEqual(new[] {TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(10)}),"GET retries bounded");
        s.Api.Add(_=>FakeHandler.State(s.Id,"NEW_STATE"));
        await Reject(()=>s.Client.QueryAsync(s.Id),MeshyError.UnknownState,"unknown task state");
        s.Api.Add(_=>new(HttpStatusCode.OK) {Content=new StringContent("{bad")});
        await Reject(()=>s.Client.QueryAsync(s.Id),MeshyError.InvalidJson,"invalid GET JSON");
        s.Api.Add(_=>FakeHandler.Json(new {id=s.Id,status="FAILED",progress=50,task_error=new {type="invalid_input",code="image_too_complex",message="DO-NOT-LOG"}}));
        await Reject(()=>s.Client.TrackAsync(s.Id),MeshyError.GenerationFailed,"failed task");
        Check((await s.Store.ReadAsync()).Single().ProviderError=="image_too_complex","safe provider code retained");
        s.Api.Add(_=>FakeHandler.State(s.Id,"CANCELED",null));
        await Reject(()=>s.Client.TrackAsync(s.Id),MeshyError.GenerationCanceled,"canceled task");
        s.Api.Add(_=>FakeHandler.State(s.Id,url:null));
        await Reject(()=>s.Client.GetResultAsync(s.Id),MeshyError.MissingResult,"missing OBJ URL");
        await Reject(()=>s.Client.QueryAsync("../../unsafe"),MeshyError.InvalidInput,"task ID cannot escape endpoint");
    }
    // HTTP-date hints are honored as well as seconds.
    using (var s = new Scenario())
    {
        s.Api.Add(_=> {var r=FakeHandler.State(s.Id,"PENDING",0);r.Headers.RetryAfter=new(DateTimeOffset.UtcNow.AddSeconds(30));return r;});
        s.Api.Add(_=>FakeHandler.State(s.Id)); await s.Client.TrackAsync(s.Id);
        Check(s.Waits.Single().TotalSeconds is > 25 and <= 30,"Retry-After HTTP date");
    }

    // Corrupt journal fails closed. It is retained for reconciliation, never silently reset.
    using (var s = new Scenario())
    {
        s.Api.Add(_=> {File.WriteAllText(Path.Combine(s.Store.Root,"tasks.json"),"{corrupt-after-acceptance");return FakeHandler.Json(new {result=s.Id},202);});
        var error=await Reject(()=>s.Client.CreateAsync(new(s.Image)),MeshyError.Storage,"accepted ID survives storage failure");
        Check(error.TaskId==s.Id,"accepted ID available to caller for manual recovery");
    }
    using (var s = new Scenario())
    {
        Directory.CreateDirectory(s.Store.Root);var path=Path.Combine(s.Store.Root,"tasks.json");await File.WriteAllTextAsync(path,"{corrupt");
        await Reject(()=>s.Client.CreateAsync(new(s.Image)),MeshyError.Storage,"corrupt journal blocks creation");
        Check(await File.ReadAllTextAsync(path)=="{corrupt" && s.Api.Calls==0,"corrupt journal retained without HTTP");
    }
    using (var s = new Scenario())
    {
        // Deliberately occupy the directory path with a file.
        File.WriteAllText(s.Store.Root,"occupied");
        await Reject(()=>s.Client.RecoverAsync(s.Id),MeshyError.Storage,"storage failure classified");
    }

    // Image validation before any HTTP or reservation.
    using (var s = new Scenario())
    {
        var image=await LocalImage.ReadAsync(s.Image);
        Check(image.Width==32 && image.Height==32 && image.MediaType=="image/png" && !image.ToString().Contains("base64"),"PNG metadata and safe ToString");
        foreach(var (extension,bytes,mime) in new[] {(".jpg",ImageFixture.Jpeg(),"image/jpeg"),(".jpeg",ImageFixture.Jpeg(),"image/jpeg"),(".webp",ImageFixture.Webp(),"image/webp")})
        {
            var path=Path.Combine(s.DirectoryPath,"valid"+extension);await File.WriteAllBytesAsync(path,bytes);
            var photo=await LocalImage.ReadAsync(path);
            Check(photo.Width==32 && photo.Height==32 && photo.MediaType==mime && photo.ToDataUri().StartsWith("data:"+mime+";base64,"),"image " + extension);
        }
        foreach(var name in new[] {"missing","empty","fake","small","pixels","large","wrong-extension","truncated","crc"})
        {
            string path=Path.Combine(s.DirectoryPath,name+".png");
            switch(name)
            {
                case "empty":await File.WriteAllBytesAsync(path,[]);break;
                case "fake":await File.WriteAllTextAsync(path,"not a picture");break;
                case "small":await File.WriteAllBytesAsync(path,ImageFixture.Png(31,32));break;
                case "pixels":await File.WriteAllBytesAsync(path,ImageFixture.Png(20000,20000));break;
                case "large":using(var file=File.Create(path))file.SetLength(LocalImage.MaxBytes+1L);break;
                case "wrong-extension":path=Path.ChangeExtension(path,".jpg");File.Copy(s.Image,path);break;
                case "truncated":await File.WriteAllBytesAsync(path,(await File.ReadAllBytesAsync(s.Image))[..40]);break;
                case "crc":var bad=await File.ReadAllBytesAsync(s.Image);bad[30]^=1;await File.WriteAllBytesAsync(path,bad);break;
            }
            await Reject(()=>s.Client.CreateAsync(new(path)),MeshyError.InvalidInput,"image " + name);
        }
        Check(s.Api.Calls==0 && (await s.Store.ReadAsync()).Count==0,"images rejected before reservation or HTTP");
    }
    await Reject(()=> {MeshyClient.CreateReal();return Task.CompletedTask;},MeshyError.RealModeDisabled,"real network disabled by default");
    using (var s = new Scenario())
    using (var real=MeshyClient.CreateRealForChecks(new FakeHandler(),new FakeHandler(),new(MeshyMode.Real,s.DirectoryPath)))
        await Reject(()=>real.CreateAsync(new(s.Image)),MeshyError.MissingCredential,"missing real credential fails before HTTP");
    using (var s = new Scenario())
    using (var real=MeshyClient.CreateRealForChecks(new FakeHandler(),new FakeHandler(),new(MeshyMode.Real,s.DirectoryPath)))
    {
        try
        {
            Environment.SetEnvironmentVariable("MESHY_API_KEY","simulation-only-not-a-real-key");
            await Reject(()=>real.CreateAsync(new(s.Image)),MeshyError.RealModeDisabled,"paid approval required before reservation or HTTP");
            var photo=await LocalImage.ReadAsync(s.Image);
            var wrong=PaidGenerationApproval.ConfirmNewPaidGeneration(photo.Sha256,new(4001));
            await Reject(()=>real.CreateAsync(new(s.Image),approval:wrong),MeshyError.RealModeDisabled,"approval bound to exact configuration");
            Environment.SetEnvironmentVariable("MESHY_API_KEY","invalid\nformat");
            await Reject(()=>real.CreateAsync(new(s.Image)),MeshyError.InvalidCredential,"credential format rejected without disclosure or HTTP");
            Check((await new JsonTaskStore(MeshyMode.Real,s.DirectoryPath).ReadAsync()).Count==0,"real guards leave no reservation");
        }
        finally {Environment.SetEnvironmentVariable("MESHY_API_KEY",null);}
    }
    using (var s = new Scenario())
    using (var other = new Scenario())
    {
        var api=new FakeHandler();api.Add(r=> {Check(r.Headers.Authorization?.Scheme=="Bearer","real policy uses Bearer over fake transport");return FakeHandler.Json(new {result=s.Id},202);});
        using var realPolicy=MeshyClient.CreateRealForChecks(api,new FakeHandler(),new(MeshyMode.Real,s.DirectoryPath));
        using var secondPolicy=MeshyClient.CreateRealForChecks(new FakeHandler(),new FakeHandler(),new(MeshyMode.Real,other.DirectoryPath));
        try
        {
            Environment.SetEnvironmentVariable("MESHY_API_KEY","simulation-only-not-a-real-key");
            var photo=await LocalImage.ReadAsync(s.Image);
            var approval=PaidGenerationApproval.ConfirmNewPaidGeneration(photo.Sha256,new());
            var accepted=await realPolicy.CreateAsync(new(s.Image),approval:approval);
            Check(accepted.TaskId==s.Id && api.Posts==1,"explicit paid policy tested entirely by simulation");
            await Reject(()=>secondPolicy.CreateAsync(new(other.Image),approval:approval),MeshyError.RealModeDisabled,"approval cannot be consumed twice");
        }
        finally {Environment.SetEnvironmentVariable("MESHY_API_KEY",null);}
    }
    using (var s = new Scenario())
    {
        s.Api.Add(_=>FakeHandler.State(s.Id));s.Files.Add(_=>FileResponse(obj));
        var first=await s.Client.GetResultAsync(s.Id);await File.WriteAllTextAsync(first.LocalPath,"tampered");
        s.Api.Add(_=>FakeHandler.State(s.Id));s.Files.Add(_=>FileResponse(obj));
        var second=await s.Client.GetResultAsync(s.Id);
        Check(second.LocalPath!=first.LocalPath && await File.ReadAllTextAsync(first.LocalPath)=="tampered" &&
            ObjInspection.Inspect(second.LocalPath)==new ObjBounds(500,500,500) && s.Api.Posts==0,"invalid cache re-downloaded by ID without overwrite or generation");
    }
    Check(Environment.GetEnvironmentVariable("MESHY_API_KEY")==null,"all simulations passed without MESHY_API_KEY");
    Console.WriteLine($"{passed} checks passed; HTTP transport: simulated handlers only; real generation POSTs: 0.");
}
finally {Environment.SetEnvironmentVariable("MESHY_API_KEY",oldKey);}

internal sealed class InlineProgress(Action<GenerationProgress> report) : IProgress<GenerationProgress>
{public void Report(GenerationProgress value)=>report(value);}
