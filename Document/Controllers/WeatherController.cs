using Microsoft.AspNetCore.Mvc;

namespace Document.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherController : ControllerBase
{
    [HttpGet]
    public string Get() => "weather";
}
