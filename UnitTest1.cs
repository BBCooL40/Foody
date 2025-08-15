#nullable enable
namespace Foody;

using NUnit.Framework;
using RestSharp;
using RestSharp.Authenticators;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;

[TestFixture]
public class FoodyTests
{
    private RestClient _client = null!;
    private const string BaseUrl = "http://softuni-qa-loadbalancer-2137572849.eu-north-1.elb.amazonaws.com:86";

    [OneTimeSetUp]
    public void Setup()
    {
        var token = GetJwtToken("AntonTzonev13", "anton13");
        _client = new RestClient(new RestClientOptions(BaseUrl)
        {
            Authenticator = new JwtAuthenticator(token)
        });
    }

    private string GetJwtToken(string username, string password)
    {
        var loginClient = new RestClient(BaseUrl);
        var request = new RestRequest("/api/User/Authentication", Method.Post)
            .AddJsonBody(new { username, password });
        var response = loginClient.Execute(request);
        var json = JsonSerializer.Deserialize<JsonElement>(response.Content ?? "{}");
        return json.GetProperty("accessToken").GetString() ?? string.Empty;
    }

    [Test,Order(1)]
    public void CreateFoodShouldReturnCreated()
    {
        // 1) ??????
        var createReq = new RestRequest("/api/Food/Create", Method.Post)
            .AddJsonBody(new { name = "New Food", description = "Delicious new food", url = "" });

        var createResp = _client.Execute(createReq);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"POST Create ????? {createResp.StatusCode}. Body: {createResp.Content}");

        // 2) ????? foodId ?? ??????
        var json = JsonSerializer.Deserialize<JsonElement>(createResp.Content ?? "{}");
        if (!json.TryGetProperty("foodId", out var idProp))
            Assert.Fail("? ???????? ?????? 'foodId'. ????: " + createResp.Content);
        var foodId = idProp.GetString();
        Assert.IsNotNull(foodId, "foodId ? null");

        // 3) ??????? Location (??? ???????? ? ??????? ??? GET)
        var location = createResp.Headers
            .FirstOrDefault(h => string.Equals(h.Name, "Location", StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString();

        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            if (TryGetOK(location!, out var _)) return;
            tried.Add(location!);
        }

        // 4) ?????? ??????? GET ??? ???? Swagger (??? ???)
        var swaggerCandidates = DiscoverFoodGetRoutesFromSwagger(foodId!);
        foreach (var path in swaggerCandidates)
        {
            if (TryGetOK(path, out var _)) return;
            tried.Add(path);
        }

        // 5) Fallback: ??????? ??????? list ????????? ? ??????? ???? ????????? ?????????? ? ???????
        var listCandidates = new[]
        {
            "/api/Food", "/api/Food/All", "/api/Food/GetAll", "/Food", "/Food/All", "/Food/GetAll"
        };

        foreach (var listPath in listCandidates.Distinct())
        {
            var resp = _client.Execute(new RestRequest(listPath, Method.Get));
            if (resp.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(resp.Content))
            {
                if (ResponseContainsId(resp.Content!, foodId!)) return;
            }
            tried.Add(listPath);
        }

        // 6) ??? ??????? ??? — ?? ????????? ??????? GET
        var sb = new StringBuilder();
        sb.AppendLine("?? ????? ?? ?????? ??????? GET ??? ?? Food. ???????:");
        sb.AppendLine(string.Join("\n", tried));
        Assert.Fail(sb.ToString());
    }

    // === ??????? ?????? ===

    // ?????? GET ?? ????? path (??????? ?), ? 3 ????? ?????. ????? true ??? 200 OK.
    private bool TryGetOK(string path, out RestResponse? last)
    {
        last = null;
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(200);
            last = _client.Execute(new RestRequest(path, Method.Get));
            if (last.StatusCode == HttpStatusCode.OK) return true;
        }
        return false;
    }

    // ????? Swagger ? ???????? ????????? ?? GET ?????? ??? Food ? id
    private IEnumerable<string> DiscoverFoodGetRoutesFromSwagger(string id)
    {
        var results = new List<string>();
        // ???-????? ??????? ?????? ?? swagger json
        var swaggerJsonPaths = new[]
        {
            "/swagger/v1/swagger.json",
            "/swagger/v2/swagger.json",
            "/swagger/swagger.json"
        };

        RestResponse? swag = null;
        foreach (var sp in swaggerJsonPaths)
        {
            swag = _client.Execute(new RestRequest(sp, Method.Get));
            if (swag.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(swag.Content)) break;
            swag = null;
        }
        if (swag == null) return results; // ???? swagger

        using var doc = JsonDocument.Parse(swag.Content!);
        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return results;

        foreach (var pathProp in paths.EnumerateObject())
        {
            var route = pathProp.Name; // ????. "/api/Food/{id}" ??? "/Food/GetById"
            if (!route.Contains("Food", StringComparison.OrdinalIgnoreCase)) continue;

            var val = pathProp.Value;

            // ????? ?? GET?
            if (!val.TryGetProperty("get", out var getOp)) continue;

            string? candidate = null;

            // 1) ??? ????? ??????? {id} -> ???????
            if (route.Contains("{id}", StringComparison.OrdinalIgnoreCase))
            {
                candidate = route.Replace("{id}", id, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 2) ??? ??????????? ???? "id" ???? query
                if (getOp.TryGetProperty("parameters", out var pars) && pars.ValueKind == JsonValueKind.Array)
                {
                    var hasId = pars.EnumerateArray()
                        .Any(p =>
                            p.TryGetProperty("name", out var n) &&
                            string.Equals(n.GetString(), "id", StringComparison.OrdinalIgnoreCase) &&
                            p.TryGetProperty("in", out var @in) &&
                            string.Equals(@in.GetString(), "query", StringComparison.OrdinalIgnoreCase)
                        );
                    if (hasId)
                    {
                        candidate = $"{route}?id={id}";
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate))
                results.Add(candidate);
        }

        // ???????? ?????
        return results.Distinct();
    }

    // ????????? ???? JSON (??? ????? ?? JSON ??????) ??????? id ? ????? ????, ????? ??????? ?? ?????????????
    private bool ResponseContainsId(string content, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
                return ObjectHasId(root, id);

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object && ObjectHasId(el, id))
                        return true;
            }
        }
        catch { /* ?? ? ??????? JSON ??? ???? ?????? */ }
        return false;
    }

    private bool ObjectHasId(JsonElement obj, string id)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (name is "id" or "foodid" or "_id")
            {
                var val = prop.Value.ToString();
                if (string.Equals(val, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
    [OneTimeTearDown]
    public void TearDown()
    {
        _client?.Dispose();
    }
    // Add a private field to store the created food ID
    private string? createdFoodId;

    // In CreateFood_ShouldReturnCreated, store the created food ID for later use
    [Test, Order(1)]
    public void CreateFood_ShouldReturnCreated()
    {
        // 1) ??????
        var createReq = new RestRequest("/api/Food/Create", Method.Post)
            .AddJsonBody(new { name = "New Food", description = "Delicious new food", url = "" });

        var createResp = _client.Execute(createReq);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"POST Create ????? {createResp.StatusCode}. Body: {createResp.Content}");

        // 2) ????? foodId ?? ??????
        var json = JsonSerializer.Deserialize<JsonElement>(createResp.Content ?? "{}");
        if (!json.TryGetProperty("foodId", out var idProp))
            Assert.Fail("? ???????? ?????? 'foodId'. ????: " + createResp.Content);
        var foodId = idProp.GetString();
        Assert.IsNotNull(foodId, "foodId ? null");

        // Store the created food ID for use in other tests
        createdFoodId = foodId;

        // 3) ??????? Location (??? ???????? ? ??????? ??? GET)
        var location = createResp.Headers
            .FirstOrDefault(h => string.Equals(h.Name, "Location", StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString();

        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            if (TryGetOK(location!, out var _)) return;
            tried.Add(location!);
        }

        // 4) ?????? ??????? GET ??? ???? Swagger (??? ???)
        var swaggerCandidates = DiscoverFoodGetRoutesFromSwagger(foodId!);
        foreach (var path in swaggerCandidates)
        {
            if (TryGetOK(path, out var _)) return;
            tried.Add(path);
        }

        // 5) Fallback: ??????? ??????? list ????????? ? ??????? ???? ????????? ?????????? ? ???????
        var listCandidates = new[]
        {
            "/api/Food", "/api/Food/All", "/api/Food/GetAll", "/Food", "/Food/All", "/Food/GetAll"
        };

        foreach (var listPath in listCandidates.Distinct())
        {
            var resp = _client.Execute(new RestRequest(listPath, Method.Get));
            if (resp.StatusCode == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(resp.Content))
            {
                if (ResponseContainsId(resp.Content!, foodId!)) return;
            }
            tried.Add(listPath);
        }

        // 6) ??? ??????? ??? — ?? ????????? ??????? GET
        var sb = new StringBuilder();
        sb.AppendLine("?? ????? ?? ?????? ??????? GET ??? ?? Food. ???????:");
        sb.AppendLine(string.Join("\n", tried));
        Assert.Fail(sb.ToString());
    }

    // In EditTheTitleOfTheFoodCreated_ShouldReturnOk, use the stored food ID
    [Test, Order(2)]
    public void EditTheTitleOfTheFoodCreated_ShouldReturnOk()
    {
        Assert.IsNotNull(createdFoodId, "No foodId available from previous test.");

        var changes = new[]
        {
            new {path = "/name", op = "replace", value = "Updated Food"},
        };
        var request = new RestRequest($"/api/Food/Edit/{createdFoodId}", Method.Patch)
          
            .AddJsonBody(changes);
        var response = _client.Execute(request);
        var json = JsonSerializer.Deserialize<JsonElement>(response.Content ?? "{}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        //Assert.That(json.GetProperty("msg"), Is.EqualTo("Successfully edited"));
    }
    [Test, Order(3)]
    public void GetAllFoods_ShouldReturnOk()
    { 
      var request = new RestRequest("/api/Food/All", Method.Get);
        var response = _client.Execute(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), 
            $"GET GetAll ????? {response.StatusCode}. Body: {response.Content}");
        
        // ???????? ???? ??? ???? ???? ???????
        Assert.IsTrue(!string.IsNullOrWhiteSpace(response.Content), "Response content is empty.");
        
        var json = JsonSerializer.Deserialize<JsonElement>(response.Content ?? "[]");
        Assert.IsTrue(json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 0, 
            "Response is not a non-empty array.");

    }
    [Test, Order(4)]
    public void DeleteFood_ShouldReturnNoContent()
    {
        var request = new RestRequest($"api/Food/Delete/{createdFoodId}", Method.Delete);
        var response = _client.Execute(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK).Or.EqualTo(HttpStatusCode.NoContent));
     }
    [Test, Order(5)]
    public void CreateFood_WithoutRequiredField_ShouldReturnBadRequest()
    {
        var food = new
        {
            Name = "",
            Description = "",

        };
        var request = new RestRequest("/api/Food/Create", Method.Post)
            .AddJsonBody(food);
        var response = _client.Execute(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"POST Create ????? {response.StatusCode}. Body: {response.Content}");
    }
    [Test, Order(6)]
    public void EditNonExistingFood_ShouldReturnNotFound()
    {
        var changes = new[]
        {
            new {path = "/name", op = "replace", value = "Updated Food"},
        };
        var request = new RestRequest("/api/Food/Edit/12345", Method.Patch)
            .AddJsonBody(changes);
        var response = _client.Execute(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            $"PATCH Edit ????? {response.StatusCode}. Body: {response.Content}");
    }
    [Test, Order(7)]
    public void DeleteNonExistingFood_ShouldReturnBadRequest()
    {
        var request = new RestRequest("/api/Food/Delete/12345", Method.Delete);
        var response = _client.Execute(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"DELETE Delete ????? {response.StatusCode}. Body: {response.Content}");
    }
}


