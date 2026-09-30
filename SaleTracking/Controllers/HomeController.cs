using Microsoft.AspNetCore.Mvc;
namespace SaleTracking.Controllers;
public class HomeController : Controller { public IActionResult Index() => User.Identity?.IsAuthenticated == true ? RedirectToAction("Index","Dashboard") : RedirectToAction("Login","Account"); }
