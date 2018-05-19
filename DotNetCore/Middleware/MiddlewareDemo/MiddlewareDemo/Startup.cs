using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Remotion.Linq.Parsing.Structure.IntermediateModel;

namespace MiddlewareDemo
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTransient<IHello, Hello>(); // <--- register my Hello class for middleware to use
            services.AddMvc();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(
            IApplicationBuilder app,  // IApplicationBuilder -> this what you add middleware to, example: app.UseStaticFiles() for serving up static files
            IHostingEnvironment env)
        {

            /*
                 ****************************
                 ** WAYS OF ADDING MIDDLEWARE : these are extension methods off of IApplicationBuilder **
                 ****************************
                 
                 NON-BRANCHING METHODS

                 Use - the most common way of adding middleware with ability to "short circut" the pipeline like when authentication fails, you don't want it to continue
                 Run - is the last middleware to be ran; aka - Terminal Middleware

                 ------------------------------------------------------
                 -- THEN NEXT METHODS ARE FOR "BRANCHING" MIDDLEWARE --
                 ------------------------------------------------------

                 BRANCHING METHODS THAT DONT REJOIN THE MAIN PIPELINE

                 Map - branch on a particular request path (example below)
                 MapWhen - branch for certain conditions contained in the httpcontext (example below)

                 BRANCHING METHOD FOR REJOINING THE PIPELINE     

                 UseWhen - similar to MapWhen, but will rejoin the pipeline
                  
             */

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //
            //  I want to first add GlobalException Handling Middleware because everything that is after this in the pipeline is wrapped in a try catch within the implementation
            //  see way below for UseRequestExceptionLogging implementation and the try catch around the next middleware to be invoked in the pipeline
            //

            // class middleware
            app.UseGlobalExceptionHandling(); // Use (don't branch) SEE BELOW FOR HOW TO CREATE YOUR OWN MIDDLEWARE LIKE THIS

            // inline middleware for branching for the "/hi" request path
            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/hi
            //
            app.Map("/hi", thisBranchesAppBuilder =>
            {
                thisBranchesAppBuilder.Run(async context =>  // add terminal inline middleware to Run
                {
                    var hello = context.RequestServices.GetService<IHello>().SayHello(); // <--- one way to get access to services is through context RequestServices

                    await context.Response.WriteAsync($"Hi There or {hello} I don't rejoin the pipeline, so you will not see I'm last in the pipeline from the main pipeline's terminal middleware below");
                });
            });

            // inline middleware for branching and rejoining the pipeline
            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/usewhen
            //
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments(new PathString("/usewhen")),
                thisBranchesAppBuilder => thisBranchesAppBuilder.Use(async (context, next) =>
                {
                    await context.Response.WriteAsync("request to /usewhen successful; you will now see the terminal middleware response...");
                    await next(); // invoke next middleware before brancing
                })
            );

            // inline middleware for branching and rejoining the pipeline
            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/usewhenexception
            //
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments(new PathString("/usewhenexception")),
                thisBranchesAppBuilder => thisBranchesAppBuilder.Use(async (context, next) =>
                {
                    // simulate an error...
                    throw new Exception("request to /usewhenexception successful, but an error occurred -> UseGlobalExceptionHandling caught this exception");
                    await next(); // invoke next middleware before brancing
                })
            );

            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/after-mapwhen?mapwhen
            //
            app.MapWhen(context => context.Request.Query.ContainsKey("mapwhen"),
                thisBranchesAppBuilder =>
                {
                    thisBranchesAppBuilder.Run(async context => await context.Response.WriteAsync("request to /mapwhen successful; you won't see I'm last in the pipeline..."));
                });

            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/after-mapwhen?mapwhen-exception
            //
            app.MapWhen(context => context.Request.Query.ContainsKey("mapwhen-exception"),
                thisBranchesAppBuilder =>
                {
                    thisBranchesAppBuilder.Run(context => throw new Exception("request with key mapwhen-exception successful, but an error occurred -> UseGlobalExceptionHandling caught this exception"));
                });

            //
            // !!!!!!! IN POSTMAN !!!!!! execute GET for http://localhost:7777/after-mapwhen
            //
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments(new PathString("/after-mapwhen")),
                thisBranchesAppBuilder => thisBranchesAppBuilder.Use(async (context, next) =>
                {
                    await context.Response.WriteAsync("request to /after-mapwhen successful; you will now see the terminal middleware response...");
                    await next(); // invoke next middleware before brancing
                })
            );

            app.UseMvc();

            //
            //  CALLING THE VALUES CONTROLLER http://localhost:7777/api/values/5  Mvc short circuts the pipeline and below doesn't get invoked
            //

            // terminal middleware
            app.Run(async context =>
            {
                await context.Response.WriteAsync("; I'M LAST IN THE PIPELINE!!!!");
            });
        }
    }

    //
    // -- ADDING MIDDLEWARE EXPLAINED BELOW -- 
    //
    // THIS IS CODE MOUNIKA AND I ADDED TO THE MIDDLEWARE REPOSITORY http://github.extendhealth.com/extend-health/middleware/pull/2 
    //

    public static class MiddlewareExtensions
    {
        //
        // ADD IApplicationBuilder extension method to use your custom middleware in the Startup Configure method
        //
        public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app)
        {
            return app.UseMiddleware<RequestExceptionMiddleware>(); // ----> UseMiddleware registers the class
        }
    }

    public class RequestExceptionMiddleware // requires a method for Invoke
    {
        private readonly RequestDelegate _next;  // ----------------------------->  this is delegate for the next item in the pipeline

        public RequestExceptionMiddleware(RequestDelegate next)  // ----> ctor must take in RequestDelegate instance
                                                                 //        you could add additional parmaters to the ctor for registered services
                                                                 //        in this example I'm doing it in the Invoke method
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));  // ---------------------> gets called on app initialization
        }

        public async Task Invoke(HttpContext httpContext, IHello hello)   // ----> Invoke requires parameter for HttpContext and must return a task
                                                                          //       IHello will be resolved by built-in DI
        {
            try
            {
                await _next.Invoke(httpContext); // ---------> try catch around the next middleware item to be invoked when a request comes in
            }
            catch (AggregateException aex)
            {
                await prepareResponse(httpContext, aex.InnerException, hello.SayHello());
            }
            catch (Exception ex)
            {
                await prepareResponse(httpContext, ex, hello.SayHello());
            }
        }

        private async Task prepareResponse(HttpContext httpContext, Exception ex, string message = "")
        {
            var inner = ex.InnerException != null
                ? $" : {ex.InnerException.Message}"
                : "";

            message += $"{ex.Message}{inner}";

            httpContext.Response.ContentType = "text/plain";
            httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            await httpContext.Response.WriteAsync(message);
        }
    }

    public interface IHello
    {
        string SayHello();
    }

    public class Hello : IHello
    {
        public string SayHello()
        {
            return "Hello! (from injected IHello) ";
        }
    }
}
