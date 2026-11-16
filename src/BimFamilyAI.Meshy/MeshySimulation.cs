using System.Net;
using System.Text.Json;

namespace BimFamilyAI.Meshy;

public enum SimulationScenario { Normal, Slow, Unauthorized, NoCredits, RateLimited, Timeout, Failed, ExpiredDownload, InvalidObj, UncertainCreate }

/// <summary>Entirely in-memory transport. The embedded OBJ is a fixture, never an AI result.</summary>
public sealed class MeshySimulation
{
    private int posts, queries, downloads;
    public int CreationRequests => Volatile.Read(ref posts);
    public int StatusRequests => Volatile.Read(ref queries);
    public int Downloads => Volatile.Read(ref downloads);
    public MeshyClient Client { get; }
    private MeshySimulation(JsonTaskStore store, SimulationScenario scenario, Func<TimeSpan, CancellationToken, Task>? delay)
        => Client = MeshyClient.CreateSimulated(new Api(this,scenario), new Files(this,scenario),store,delay);
    public static MeshySimulation Create(JsonTaskStore store, SimulationScenario scenario = SimulationScenario.Normal,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(store,scenario,delay);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8,"application/json") };
    private sealed class Api(MeshySimulation owner, SimulationScenario scenario) : SimulatedMeshyHandler
    {
        private readonly Dictionary<string,int> polls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref owner.posts);
                if (scenario == SimulationScenario.Timeout) throw new TaskCanceledException();
                if (scenario == SimulationScenario.UncertainCreate) throw new HttpRequestException("Simulated ambiguous creation");
                int error = scenario switch { SimulationScenario.Unauthorized => 401, SimulationScenario.NoCredits => 402, _ => 0 };
                if (error != 0) return Task.FromResult(Json(new { message="Simulated rejection" },(HttpStatusCode)error));
                return Task.FromResult(Json(new {result=Guid.NewGuid().ToString()},HttpStatusCode.Accepted));
            }
            Interlocked.Increment(ref owner.queries);
            if (scenario == SimulationScenario.RateLimited)
            {
                var limited=Json(new {message="Simulated rate limit"},HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter=new(TimeSpan.FromSeconds(5));return Task.FromResult(limited);
            }
            string id=request.RequestUri!.Segments[^1];
            int count=polls.GetValueOrDefault(id)+1;polls[id]=count;
            string status=scenario == SimulationScenario.Failed ? "FAILED" : scenario == SimulationScenario.Slow || count==1 ? "PENDING" : count==2 ? "IN_PROGRESS" : "SUCCEEDED";
            return Task.FromResult(Json(new {id,status,progress=status=="SUCCEEDED"?100:status=="IN_PROGRESS"?50:0,
                created_at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                model_urls=new {obj="https://assets.meshy.ai/simulation/model.obj"},
                task_error=status=="FAILED" ? new {type="invalid_input",code="image_too_complex"} : null}));
        }
    }
    private sealed class Files(MeshySimulation owner, SimulationScenario scenario) : SimulatedMeshyHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Interlocked.Increment(ref owner.downloads);
            if (request.Headers.Authorization is not null) throw new InvalidOperationException("Storage must not receive authorization");
            if (scenario==SimulationScenario.ExpiredDownload) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (scenario==SimulationScenario.InvalidObj) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("Invalid simulated OBJ")});
            var resource=typeof(MeshySimulation).Assembly.GetManifestResourceStream("BimFamilyAI.Meshy.SimulationCube.obj")
                ?? throw new InvalidOperationException("Missing simulation fixture");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StreamContent(resource)});
        }
    }
}
