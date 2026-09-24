using Microsoft.AspNetCore.Mvc;

public sealed class HomeController : Controller {
    [HttpGet]
    public IActionResult Index() => View();
}
