using System.Globalization;
using System.Xml.Linq;
using Macast.Core.Utils;

namespace Macast.Core.Upnp;

public static class ScpdLoader
{
    private static readonly XNamespace ServiceNs = "urn:schemas-upnp-org:service-1-0";
    private static readonly XNamespace DeviceNs = "urn:schemas-upnp-org:device-1-0";

    public static Dictionary<string, ServiceDefinition> Load(string? xmlDir = null)
    {
        xmlDir ??= AssetPaths.XmlDir;
        var result = new Dictionary<string, ServiceDefinition>(StringComparer.Ordinal);
        var descriptionPath = Path.Combine(xmlDir, "Description.xml");
        var description = XDocument.Load(descriptionPath);

        foreach (var serviceType in description.Descendants(DeviceNs + "serviceType"))
        {
            var ns = serviceType.Value.Trim();
            var parts = ns.Split(':');
            if (parts.Length < 4)
            {
                throw new InvalidDataException($"invalid service type: {ns}");
            }

            var service = parts[3];
            var scpdPath = Path.Combine(xmlDir, service + ".xml");
            var doc = XDocument.Load(scpdPath);
            result[service] = Parse(service, ns, doc);
        }

        return result;
    }

    private static ServiceDefinition Parse(string service, string ns, XDocument doc)
    {
        var definition = new ServiceDefinition { Name = service, Namespace = ns };

        foreach (var element in doc.Descendants(ServiceNs + "stateVariable"))
        {
            var name = element.Element(ServiceNs + "name")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var dataType = element.Element(ServiceNs + "dataType")?.Value.Trim() ?? "string";
            var sendEvents = string.Equals(element.Attribute("sendEvents")?.Value, "yes", StringComparison.OrdinalIgnoreCase);
            var variable = new StateVariable
            {
                Name = name,
                DataType = dataType,
                SendEvents = sendEvents,
                Service = service,
                DefaultValue = element.Element(ServiceNs + "defaultValue")?.Value
            };

            var allowed = element.Element(ServiceNs + "allowedValueList");
            if (allowed is not null)
            {
                foreach (var value in allowed.Elements(ServiceNs + "allowedValue"))
                {
                    if (value.Value is { Length: > 0 } text)
                    {
                        variable.AllowedValues.Add(text);
                    }
                }
            }

            var range = element.Element(ServiceNs + "allowedValueRange");
            if (range is not null)
            {
                if (long.TryParse(range.Element(ServiceNs + "minimum")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var min))
                {
                    variable.Minimum = min;
                }

                if (long.TryParse(range.Element(ServiceNs + "maximum")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var max))
                {
                    variable.Maximum = max;
                }
            }

            // Apply the SCPD default, then the "NOT_IMPLEMENTED" convention used by
            // several UPnP stacks when an allowed value list is present.
            variable.Value = variable.DefaultValue ?? string.Empty;
            if (variable.AllowedValues.Count > 0 && variable.Value.Length == 0)
            {
                variable.Value = variable.AllowedValues.Contains("NOT_IMPLEMENTED")
                    ? "NOT_IMPLEMENTED"
                    : variable.AllowedValues[0];
            }

            definition.StateVariables[name] = variable;
        }

        foreach (var element in doc.Descendants(ServiceNs + "action"))
        {
            var name = element.Element(ServiceNs + "name")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var action = new UpnpAction { Name = name };
            var arguments = element.Element(ServiceNs + "argumentList");
            if (arguments is not null)
            {
                foreach (var argument in arguments.Elements(ServiceNs + "argument"))
                {
                    var argName = argument.Element(ServiceNs + "name")?.Value.Trim();
                    var direction = argument.Element(ServiceNs + "direction")?.Value.Trim() ?? "in";
                    var related = argument.Element(ServiceNs + "relatedStateVariable")?.Value.Trim();
                    if (string.IsNullOrEmpty(argName) || string.IsNullOrEmpty(related))
                    {
                        continue;
                    }

                    var parsed = new ActionArgument
                    {
                        Name = argName,
                        Direction = direction,
                        RelatedStateVariable = related
                    };

                    if (string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase))
                    {
                        action.Inputs.Add(parsed);
                    }
                    else
                    {
                        action.Outputs.Add(parsed);
                    }
                }
            }

            definition.Actions[name] = action;
        }

        return definition;
    }
}
