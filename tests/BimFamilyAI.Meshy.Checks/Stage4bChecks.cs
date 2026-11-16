using BimFamilyAI.Core;
using BimFamilyAI.Meshy;

internal static class Stage4bChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        async Task Reject(Func<Task> operation,MeshyError error,string name)
        {
            try {await operation();}catch(MeshyException ex) {check(ex.Code==error,"4B "+name);return;}
            throw new Exception("Expected rejection: "+name);
        }
        static Task NoWait(TimeSpan time,CancellationToken token) {token.ThrowIfCancellationRequested();return Task.CompletedTask;}
        using(var s=new Scenario())
        {
            var simulation=MeshySimulation.Create(s.Store,delay:NoWait);
            using var session=new PhotoGenerationSession(simulation.Client,s.Store);
            var value=await session.GenerateAsync(s.Image,new(),(_,_)=>throw new Exception("Simulation must not request paid approval"));
            check(File.Exists(value.LocalPath) && value.Bounds==new ObjBounds(500,500,500),"4B runtime simulator PNG → OBJ");
            check(simulation.CreationRequests==1 && simulation.StatusRequests==4 && simulation.Downloads==1,"4B one simulated POST and finite polling");
            var reused=await session.GenerateAsync(s.Image,new());
            check(reused.LocalPath==value.LocalPath && simulation.CreationRequests==1 && simulation.Downloads==1 && simulation.StatusRequests==4,"4B repeated generation reuses OBJ without HTTP");
            var request=PhotoFamilyRequest.Create(value,750,ObjVerticalAxis.Z,"FromPhoto","test",true);
            check(request.TargetHeightMm==750 && request.SourceUnitsUnspecified && request.FamilyName=="SIM_FromPhoto" && request.Description.StartsWith("SIMULACIÓN"),"4B physical height and simulated metadata");
            foreach(double bad in new[] {0d,-1,double.NaN,double.PositiveInfinity})
            {
                try {PhotoFamilyRequest.Create(value,bad,ObjVerticalAxis.Z,"FromPhoto","",false);throw new Exception("Invalid height accepted");}
                catch(ArgumentException) {check(true,"4B invalid physical height rejected");}
            }
            try {PhotoFamilyRequest.Create(value,500,ObjVerticalAxis.Z,"bad/name","",false);throw new Exception("Invalid name accepted");}
            catch(IOException) {check(session.Result?.LocalPath==value.LocalPath && simulation.CreationRequests==1,"4B conversion validation failure preserves task and OBJ");}
            var good=PhotoFamilyRequest.Create(value,500,ObjVerticalAxis.Z,"Recovered","",true);
            check(File.Exists(good.Path) && simulation.CreationRequests==1,"4B corrected conversion reuses same OBJ");
            string journal=await File.ReadAllTextAsync(Path.Combine(s.Store.Root,"tasks.json"));
            check(!journal.Contains("data:image") && !journal.Contains("https:") && !journal.Contains("Authorization"),"4B runtime journal contains no image, signed URL or Bearer");
        }
        using(var s=new Scenario())
        using(var cancel=new CancellationTokenSource())
        {
            var slow=MeshySimulation.Create(s.Store,SimulationScenario.Slow,(_,token)=> {cancel.Cancel();token.ThrowIfCancellationRequested();return Task.CompletedTask;});
            using(var session=new PhotoGenerationSession(slow.Client,s.Store))
            {
                try {await session.GenerateAsync(s.Image,new(),cancellationToken:cancel.Token);throw new Exception("Cancellation ignored");}
                catch(OperationCanceledException) {check(session.LastTaskId is not null && slow.CreationRequests==1,"4B local cancellation retains accepted ID");}
            }
            var id=(await s.Store.ReadAsync()).Single().TaskId!;
            var restarted=MeshySimulation.Create(new(dataDirectory:s.DirectoryPath),delay:NoWait);
            using var resumed=new PhotoGenerationSession(restarted.Client,s.Store);
            var result=await resumed.ResumeAsync(id);
            check(File.Exists(result.LocalPath) && restarted.CreationRequests==0,"4B resume after close/restart uses ID only");
        }
        using(var s=new Scenario())
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var simulation=MeshySimulation.Create(s.Store,delay:(_,_)=> {entered.TrySetResult();return release.Task;});
            using var session=new PhotoGenerationSession(simulation.Client,s.Store);
            var first=session.GenerateAsync(s.Image,new());await entered.Task;
            await Reject(()=>session.GenerateAsync(s.Image,new()),MeshyError.Busy,"double click blocked");
            release.SetResult();await first;
            check(simulation.CreationRequests==1,"4B double click creates only one task");
        }
        using(var s=new Scenario())
        {
            var api=new FakeHandler();using var real=MeshyClient.CreateRealForChecks(api,new FakeHandler(),new(MeshyMode.Real,s.DirectoryPath));
            using var session=new PhotoGenerationSession(real,new(MeshyMode.Real,s.DirectoryPath));
            await Reject(()=>session.GenerateAsync(s.Image,new(),(_,_)=>throw new Exception("No confirmation before checking missing credential")),MeshyError.MissingCredential,"missing credential before confirmation/send");
            check(api.Calls==0 && (await session.HistoryAsync()).Count==0,"4B real policy cannot send or reserve without credential");
        }
        foreach(var (scenario,error,known) in new[] {
            (SimulationScenario.Unauthorized,MeshyError.InvalidCredential,false),(SimulationScenario.NoCredits,MeshyError.NoCredits,false),
            (SimulationScenario.RateLimited,MeshyError.RateLimited,true),(SimulationScenario.Timeout,MeshyError.NetworkTimeout,false),
            (SimulationScenario.Failed,MeshyError.GenerationFailed,true),(SimulationScenario.ExpiredDownload,MeshyError.ExpiredUrl,true),
            (SimulationScenario.InvalidObj,MeshyError.InvalidObj,true)})
        {
            using var s=new Scenario();var simulation=MeshySimulation.Create(s.Store,scenario,NoWait);
            using var session=new PhotoGenerationSession(simulation.Client,s.Store);
            await Reject(()=>session.GenerateAsync(s.Image,new()),error,"runtime scenario "+scenario);
            var record=(await s.Store.ReadAsync()).Single();
            check(simulation.CreationRequests==1 && (record.TaskId is not null)==known && session.Result is null,"4B error conserves task/reservation "+scenario);
        }
        using(var s=new Scenario())
        {
            var failed=MeshySimulation.Create(s.Store,SimulationScenario.UncertainCreate,NoWait);
            using(var session=new PhotoGenerationSession(failed.Client,s.Store))
                await Reject(()=>session.GenerateAsync(s.Image,new()),MeshyError.NetworkFailure,"uncertain reservation");
            var record=(await s.Store.ReadAsync()).Single();var id=Guid.NewGuid().ToString();
            var normal=MeshySimulation.Create(s.Store,delay:NoWait);using var session2=new PhotoGenerationSession(normal.Client,s.Store);
            await Reject(()=>session2.ReconcileAsync(record.Fingerprint,id,false),MeshyError.InvalidInput,"reconciliation requires evidence");
            var linked=await session2.ReconcileAsync(record.Fingerprint,id,true);
            check(linked.Audit.Single().Action==TaskAuditAction.LinkedRecoveredId && linked.ImageHash==record.ImageHash && linked.TaskId==id,"4B reconciliation keeps trace and image association");
            var result=await session2.GenerateAsync(s.Image,new());
            check(File.Exists(result.LocalPath) && normal.CreationRequests==0 && (await s.Store.ReadAsync()).Count==1,"4B reconciled creation resumes without new POST or duplicate record");
        }
        using(var s=new Scenario())
        {
            var rejected=MeshySimulation.Create(s.Store,SimulationScenario.Unauthorized,NoWait);
            using(var first=new PhotoGenerationSession(rejected.Client,s.Store))
                await Reject(()=>first.GenerateAsync(s.Image,new()),MeshyError.InvalidCredential,"confirmed rejected attempt");
            var normal=MeshySimulation.Create(s.Store,delay:NoWait);using var session=new PhotoGenerationSession(normal.Client,s.Store);
            await Reject(()=>session.GenerateAsync(s.Image,new()),MeshyError.Duplicate,"rejected attempt never retried automatically");
            var selected=(await s.Store.ReadAsync()).Single();
            await Reject(()=>session.GenerateAsync(s.Image,new(4001),retryRejected:true,expectedRejectedFingerprint:selected.Fingerprint),MeshyError.InvalidInput,"selected rejection must match photo and profile");
            check(normal.CreationRequests==0 && (await s.Store.ReadAsync()).Count==1,"4B history mismatch creates no task or reservation");
            var result=await session.GenerateAsync(s.Image,new(),retryRejected:true);
            var record=(await s.Store.ReadAsync()).Single();
            check(record.Audit.Single().Action==TaskAuditAction.RetriedConfirmedRejection && record.Audit[0].PreviousError==MeshyError.InvalidCredential && normal.CreationRequests==1,"4B explicit corrected retry preserves rejection audit");
            check(File.Exists(result.LocalPath),"4B corrected retry completes simulation");
        }
        using(var s=new Scenario())
        {
            var uncertain=MeshySimulation.Create(s.Store,SimulationScenario.UncertainCreate,NoWait);
            using var session=new PhotoGenerationSession(uncertain.Client,s.Store);
            await Reject(()=>session.GenerateAsync(s.Image,new()),MeshyError.NetworkFailure,"uncertain simulated send");
            await Reject(()=>session.GenerateAsync(s.Image,new(),retryRejected:true),MeshyError.InvalidInput,"uncertain attempt cannot use rejected retry");
            check(uncertain.CreationRequests==1,"4B uncertain attempt still has exactly one POST");
        }
        using(var s=new Scenario())
        {
            var api=new FakeHandler();api.Add(_=>FakeHandler.Json(new {message="rejected"},401));
            var realStore=new JsonTaskStore(MeshyMode.Real,s.DirectoryPath);
            using var real=MeshyClient.CreateRealForChecks(api,new FakeHandler(),realStore);
            try
            {
                Environment.SetEnvironmentVariable("MESHY_API_KEY","simulation-only-not-a-real-key");
                var image=await LocalImage.ReadAsync(s.Image);var options=new MeshyOptions();
                var first=PaidGenerationApproval.ConfirmNewPaidGeneration(image.Sha256,options);
                await Reject(()=>real.CreateAsync(new(s.Image),options,first),MeshyError.InvalidCredential,"real rejection entirely through fake transport");
                await Reject(()=>real.RetryRejectedAsync(new(s.Image),options,false),MeshyError.InvalidInput,"retry needs explicit correction");
                await Reject(()=>real.RetryRejectedAsync(new(s.Image),options,true),MeshyError.RealModeDisabled,"real retry needs fresh paid approval");
                await Reject(()=>real.RetryRejectedAsync(new(s.Image),options,true,first),MeshyError.RealModeDisabled,"previous paid approval cannot authorize a retry");
                api.Add(_=>FakeHandler.Json(new {result=s.Id},202));
                var accepted=await real.RetryRejectedAsync(new(s.Image),options,true,PaidGenerationApproval.ConfirmNewPaidGeneration(image.Sha256,options));
                check(accepted.TaskId==s.Id && api.Posts==2 && accepted.Audit.Length==1,"4B corrected real policy needs two distinct approvals; both POSTs simulated");
            }
            finally {Environment.SetEnvironmentVariable("MESHY_API_KEY",null);}
        }
        using(var s=new Scenario())
        {
            var api=new FakeHandler();var realStore=new JsonTaskStore(MeshyMode.Real,s.DirectoryPath);
            using var session=new PhotoGenerationSession(MeshyClient.CreateRealForChecks(api,new FakeHandler(),realStore),realStore);
            try
            {
                Environment.SetEnvironmentVariable("MESHY_API_KEY","simulation-only-not-a-real-key");
                try {await session.GenerateAsync(s.Image,new(),(_,_)=>Task.FromResult<PaidGenerationApproval?>(null));throw new Exception("Declined approval was ignored");}
                catch(OperationCanceledException) {check(api.Calls==0 && (await realStore.ReadAsync()).Count==0,"4B declining payment creates no reservation or POST");}
                await Reject(()=>session.GenerateAsync(s.Image,new(),async (image,options)=>
                {
                    await File.WriteAllBytesAsync(s.Image,ImageFixture.Png(33,32));
                    return PaidGenerationApproval.ConfirmNewPaidGeneration(image.Sha256,options);
                }),MeshyError.RealModeDisabled,"photo changed after confirmation invalidates paid approval");
                check(api.Calls==0 && (await realStore.ReadAsync()).Count==0,"4B changed photo cannot send approved old content");
            }
            finally {Environment.SetEnvironmentVariable("MESHY_API_KEY",null);}
        }
        foreach(var (status,error) in new[] {(408,MeshyError.NetworkTimeout),(409,MeshyError.CreationUncertain)})
        {
            using var s=new Scenario();s.Api.Add(_=>FakeHandler.Json(new {message="ambiguous"},status));
            await Reject(()=>s.Client.CreateAsync(new(s.Image)),error,"HTTP "+status+" is not a confirmed rejection");
            await Reject(()=>s.Client.RetryRejectedAsync(new(s.Image),new(),true),MeshyError.InvalidInput,"HTTP "+status+" cannot unlock retry");
            check(s.Api.Posts==1 && (await s.Store.ReadAsync()).Single().Submission==SubmissionState.Uncertain,"4B ambiguous HTTP preserves reservation");
        }
        using(var s=new Scenario())
        {
            s.Api.Add(_=>FakeHandler.Json(new {message="wait"},429,60));
            await Reject(()=>s.Client.CreateAsync(new(s.Image)),MeshyError.RateLimited,"POST Retry-After stored");
            var record=(await s.Store.ReadAsync()).Single();
            check(record.RetryNotBefore>DateTimeOffset.UtcNow,"4B Retry-After survives restart in journal");
            using var restarted=MeshyClient.CreateSimulated(new FakeHandler(),new FakeHandler(),new(dataDirectory:s.DirectoryPath));
            await Reject(()=>restarted.RetryRejectedAsync(new(s.Image),new(),true),MeshyError.RateLimited,"manual retry cannot bypass persisted Retry-After");
            check(s.Api.Posts==1 && (await s.Store.ReadAsync()).Single().Audit.Length==0,"4B waiting does not create an attempt or consume approval");
        }
    }
}
