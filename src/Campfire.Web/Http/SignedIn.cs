namespace Campfire.Web.Http;

public static class SignedIn
{
    public static long? UserId(HttpContext context)
    {
        return context.Items[CampfirePipeline.UserIdItem] is long id ? id : null;
    }
}
