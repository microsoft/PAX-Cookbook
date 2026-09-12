using System.Text;

namespace PAXCookbook.App;

// App-only half of JsonModel. The portable core lives in
// PAXCookbook.Shared/Contracts/AppSharedSource/JsonModel.cs and is compiled by
// both the App and Setup; this partial adds the one member that needs ASP.NET
// hosting types, so Setup never pulls in a web dependency.
internal static partial class JsonModel
{
    // Reads the request body and parses it into the CLR tree.
    public static async Task<object?> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        string raw = await reader.ReadToEndAsync();
        return Parse(raw);
    }
}
