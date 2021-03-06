﻿/*
 * CHANGE LOG - keep only last 5 threads
 * 
 * RESSOURCES
 */
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino.Agent.Domain;
using Rhino.Agent.Extensions;
using Rhino.Agent.Models;
using Rhino.Api.Contracts.AutomationProvider;
using Rhino.Api.Contracts.Configuration;
using Rhino.Api.Extensions;
using Rhino.Api.Parser.Contracts;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Rhino.Agent.Controllers
{
    [Route("api/v3/[controller]")]
    [Route("api/latest/[controller]")]
    [ApiController]
    public class RhinoController : ControllerBase
    {
        // constants
        private const StringComparison Compare = StringComparison.OrdinalIgnoreCase;
        private readonly string Seperator =
            Environment.NewLine + Environment.NewLine + SpecSection.Separator + Environment.NewLine + Environment.NewLine;

        // members: state
        private readonly IEnumerable<Type> types;
        private readonly RhinoConfigurationRepository configurationRepository;
        private readonly RhinoModelRepository modelRepository;
        private readonly RhinoTestCaseRepository testCaseRepository;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly IConfiguration appSettings;
        private readonly IServiceProvider provider;

        /// <summary>
        /// Creates a new instance of Rhino.Agent.Controllers.RhinoController.
        /// </summary>
        /// <param name="provider">Services container.</param>
        public RhinoController(IServiceProvider provider)
        {
            types = provider.GetRequiredService<IEnumerable<Type>>();
            configurationRepository = provider.GetRequiredService<RhinoConfigurationRepository>();
            modelRepository = provider.GetRequiredService<RhinoModelRepository>();
            testCaseRepository = provider.GetRequiredService<RhinoTestCaseRepository>();
            jsonSettings = provider.GetRequiredService<JsonSerializerSettings>();
            appSettings = provider.GetRequiredService<IConfiguration>();
            this.provider = provider;
        }

        #region *** By Configuration  ***
        // GET api/v3/rhino/configurations/<id>
        [HttpGet("configurations/{configuration}")]
        public IActionResult ExecuteByConfiguration(string configuration)
        {
            // get configuration
            var (statusCode, onConfiguration) = GetConfiguration(configuration, allowNoTests: false);

            // failure response
            if (statusCode > HttpStatusCode.OK.ToInt32())
            {
                return new ContentResult { StatusCode = statusCode };
            }

            // process request
            return ExecuteConfigurations(new[] { onConfiguration });
        }

        // POST api/v3/rhino/configurations
        [HttpPost("configurations")]
        public IActionResult ExecuteByConfiguration()
        {
            return DoExecute();
        }

        // POST api/v3/rhino/execute
        [HttpPost("execute")]
        public IActionResult Execute()
        {
            try
            {
                // get configuration
                var requestBody = Request.ReadAsync().GetAwaiter().GetResult();
                var configuration = JsonConvert.DeserializeObject<RhinoConfiguration>(requestBody).ApplySettings(appSettings);

                // get provider capabilities
                var jsonObject = JObject.Parse(requestBody);
                var token = jsonObject.SelectToken("..providerConfiguration.capabilities");
                configuration.ProviderConfiguration.Capabilities = token != null
                    ? JsonConvert.DeserializeObject<IDictionary<string, object>>($"{token}")
                    : new Dictionary<string, object>();

                // execute
                var response = configuration.Execute(types);

                // response
                return new ContentResult
                {
                    Content = JsonConvert.SerializeObject(response),
                    ContentType = MediaTypeNames.Application.Json,
                    StatusCode = HttpStatusCode.OK.ToInt32()
                };
            }
            catch (Exception e) when (e != null)
            {
                return new ContentResult
                {
                    Content = $"{e}",
                    ContentType = MediaTypeNames.Text.Plain,
                    StatusCode = HttpStatusCode.InternalServerError.ToInt32()
                };
            }
        }

        // POST api/v3/rhino/configurations
        [HttpPost("configurations/ids")]
        public IActionResult ExecuteByConfigurationIds()
        {
            return DoExecute();
        }

        // POST api/v3/rhino/configurations/<id>
        [HttpPost("configurations/{configuration}")]
        public async Task<IActionResult> ExecuteByConfigurationAndSpec(string configuration)
        {
            // get configuration
            var (statusCode, onConfiguration) = GetConfiguration(configuration, allowNoTests: true);

            // failure response
            if (statusCode > HttpStatusCode.OK.ToInt32())
            {
                return new ContentResult { StatusCode = statusCode };
            }

            // build
            var requestBody = await Request.ReadAsync().ConfigureAwait(false);
            onConfiguration.TestsRepository = requestBody.Split(Seperator).Where(i => !string.IsNullOrEmpty(i));

            // process request
            return ExecuteConfigurations(new[] { onConfiguration });
        }

        private IActionResult DoExecute()
        {
            // get configurations
            var cofigurations = Request.GetConfigurations(provider);

            // failure response
            if (!cofigurations.Any())
            {
                return NotFound(new { Message = "No configurations found or provided." });
            }

            // process request
            return ExecuteConfigurations(cofigurations);
        }
        #endregion

        #region *** By Collection     ***
        // GET api/v3/rhino/collections/<id>
        [HttpGet("collections/{collection}")]
        public IActionResult ExecuteByCollection(string collection)
        {
            return ByCollection(collection, configuration: string.Empty);
        }

        // GET api/v3/rhino/collections/<id>/configurations/<id>
        [HttpGet("collections/{collection}/configurations/{configuration}")]
        public IActionResult ExecuteByCollection(string collection, string configuration)
        {
            return ByCollection(collection, configuration);
        }

        private IActionResult ByCollection(string collection, string configuration)
        {
            // setup
            var credentials = Request.GetAuthentication();

            // get test collection
            var (statusCode, onCollection) = testCaseRepository.Get(credentials, id: collection);
            if (statusCode != HttpStatusCode.OK)
            {
                return new ContentResult { StatusCode = statusCode.ToInt32() };
            }

            // setup configurations > setup scenarios
            var onCofigurations = string.IsNullOrEmpty(configuration)
                ? Get(onCollection)
                : Get(onCollection, configuration);

            var onScenarios = onCollection
                .RhinoTestCaseDocuments
                .SelectMany(i => i.RhinoSpec.Split(">>>").Select(j => j.Trim()));

            // override configuration
            foreach (var onConfiguration in onCofigurations)
            {
                onConfiguration.TestsRepository = onScenarios.ToArray();
            }

            // process request
            return ExecuteConfigurations(onCofigurations);
        }

        private IEnumerable<RhinoConfiguration> Get(RhinoTestCaseCollection collection) => collection
            .Configurations
            .Select(i=> GetConfiguration(i, allowNoTests: false))
            .Where(i => i.statusCode == 200)
            .Select(i => i.configuration);

        private IEnumerable<RhinoConfiguration> Get(RhinoTestCaseCollection collection, string configuration) => collection
            .Configurations
            .Select(i=> GetConfiguration(i, allowNoTests: false))
            .Where(i => i.statusCode == 200 && $"{i.configuration.Id}".Equals(configuration, Compare))
            .Select(i => i.configuration);
        #endregion

        // execute configurations against Rhino engine
        private IActionResult ExecuteConfigurations(IEnumerable<RhinoConfiguration> configurations)
        {
            // process request
            var testRuns = new ConcurrentBag<RhinoTestRun>();
            foreach (var configuration in configurations)
            {
                var onConfiguration = configuration.ApplySettings(appSettings);

                var testRun = onConfiguration.Execute(types);
                testRuns.Add(testRun);
            }

            // process response body
            var content = testRuns.Count == 1
                ? JsonConvert.SerializeObject(testRuns.ElementAt(0), jsonSettings)
                : JsonConvert.SerializeObject(testRuns, jsonSettings);

            // compose response
            return new ContentResult
            {
                Content = content,
                ContentType = MediaTypeNames.Application.Json,
                StatusCode = HttpStatusCode.OK.ToInt32()
            };
        }

        #region *** GET Configuration ***
        // get ready to run configuration, by ID
        private (int statusCode, RhinoConfiguration configuration) GetConfiguration(string id, bool allowNoTests)
        {
            // setup
            var credentials = Request.GetAuthentication();
            var (statusCode, configuration) = configurationRepository.Get(credentials, id);

            // not found conditions
            if (statusCode != HttpStatusCode.OK)
            {
                return (HttpStatusCode.NotFound.ToInt32(), default);
            }

            // populate scenarios > populate elements
            configuration.TestsRepository = GetTests(configuration).ToArray();
            configuration.Models = GetModels(configuration).ToArray();

            // bad request conditions
            var isTests = configuration.TestsRepository.Any();
            if (!isTests && !allowNoTests)
            {
                return (HttpStatusCode.BadRequest.ToInt32(), configuration);
            }

            // results
            return (HttpStatusCode.OK.ToInt32(), configuration);
        }

        // collect scenarios for configuration
        private IEnumerable<string> GetTests(RhinoConfiguration configuration)
        {
            // setup
            var credentials = Request.GetAuthentication();
            var scenarios = new List<string>();

            // collection
            foreach (var id in configuration.TestsRepository)
            {
                if (!Regex.IsMatch(input: id, pattern: @"\w{8}-(\w{4}-){3}\w{12}"))
                {
                    continue;
                }
                var (statusCode, scenariosCollection) = testCaseRepository.Get(credentials, id);
                if (statusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }
                var onScenarios = scenariosCollection
                    .RhinoTestCaseDocuments
                    .SelectMany(i => i.RhinoSpec.Split(">>>").Select(j => j.Trim()));
                scenarios.AddRange(onScenarios);
            }

            // results
            return scenarios;
        }

        // collect elements for configuration
        private IEnumerable<string> GetModels(RhinoConfiguration configuration)
        {
            // setup
            var credentials = Request.GetAuthentication();
            var elements = new List<string>();

            // collection
            foreach (var id in configuration.Models)
            {
                if (!Regex.IsMatch(input: id, pattern: @"\w{8}-(\w{4}-){3}\w{12}"))
                {
                    continue;
                }
                var (statusCode, modelsCollection) = modelRepository.Get(credentials, id);
                if (statusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }
                elements.Add(JsonConvert.SerializeObject(modelsCollection.Models));
            }

            // results
            return elements;
        }
        #endregion
    }
}