// Top-level statements generate an internal Program class. WebApplicationFactory<Program>
// in GatewayService.IntegrationTests needs it public: a public class (GatewayApiFactory)
// can't inherit a generic base instantiated with an internal type argument — that's a
// compile-time accessibility check InternalsVisibleTo alone doesn't satisfy. This partial
// declaration is enough on its own to make the merged Program class public.
public partial class Program { }
