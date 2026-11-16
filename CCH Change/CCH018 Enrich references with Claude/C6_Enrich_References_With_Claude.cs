using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Net;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

using SwissAcademic.Citavi;
using SwissAcademic.Citavi.Metadata;
using SwissAcademic.Citavi.Shell;
using SwissAcademic.Collections;

// Implementation of macro editor is preliminary and experimental.
// The Citavi object model is subject to change in future version.

public static class CitaviMacro
{
	public static void Main()
	{
		//****************************************************************************************************************
		// ENRICH REFERENCES WITH CLAUDE
		// 1.0 - 2026-11-16
		//
		// Sends the linked PDF of each selected reference to Claude via a LiteLLM proxy
		// (Anthropic Messages format, /v1/messages) and
		// - writes an abstract (only if the abstract field is empty)
		// - adds direct quotations with page numbers
		// - adds summaries of sections with page ranges
		// Existing abstracts and quotations are never changed: the abstract is only written if
		// the field is empty, quotations and summaries only if the reference has none yet.
		// References with both an abstract and quotations are not sent at all.
		//
		// Requires two Windows user environment variables:
		// - LITELLM_BASE_URL: base URL of the LiteLLM proxy, e.g. https://litellm.example.com
		// - LITELLM_API_KEY:  the LiteLLM (virtual) key
		// To call Anthropic directly instead, set the base URL to https://api.anthropic.com
		// and the key variable to an Anthropic API key.
		// The PDFs are sent to the proxy and the model provider. Make sure you are allowed to do this.
		//
		// EDIT HERE
		// Variables to be changed by user

		string baseUrlVariable = "LITELLM_BASE_URL";			// environment variable holding the proxy base URL
		string apiKeyVariable = "LITELLM_API_KEY";				// environment variable holding the key
		string model = "claude-sonnet-5-5";						// model name as configured in LiteLLM
		string effort = "high";						// low, medium, high, xhigh, max
		int maxQuotations = 15;						// maximum number of direct quotations per reference
		int maxSummaries = 8;						// maximum number of summaries per reference
		string markerKeyword = "AI-generated";		// keyword added to all new quotations; "" = no keyword
		int maxPdfMB = 20;							// larger PDFs are skipped (API limit: 32 MB per request incl. base64 overhead)

		// DO NOT EDIT BELOW THIS LINE
		// ****************************************************************************************************************

		if (Program.ProjectShells.Count == 0) return;		//no project open

		string baseUrl = GetEnvironmentSetting(baseUrlVariable);
		string apiKey = GetEnvironmentSetting(apiKeyVariable);
		List<string> missing = new List<string>();
		if (baseUrl == null) missing.Add(baseUrlVariable);
		if (apiKey == null) missing.Add(apiKeyVariable);
		if (missing.Count > 0)
		{
			MessageBox.Show("The environment variable(s) " + String.Join(", ", missing) + " are not set.\r\n\r\nSet them as Windows user environment variables and restart Citavi.", "Macro", MessageBoxButtons.OK, MessageBoxIcon.Error);
			return;
		}

		//accept the base URL with or without trailing slash or /v1/messages
		string apiUrl = baseUrl.TrimEnd('/');
		if (!apiUrl.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) apiUrl += "/v1/messages";
		Uri apiUri;
		if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out apiUri) || (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp))
		{
			MessageBox.Show("The environment variable " + baseUrlVariable + " does not contain a valid URL:\r\n" + baseUrl, "Macro", MessageBoxButtons.OK, MessageBoxIcon.Error);
			return;
		}

		if (IsBackupAvailable() == false) return;			//user wants to backup his/her project first

		//reference to active Project
		Project activeProject = Program.ActiveProjectShell.Project;
		if (activeProject == null) return;

		//iterate over the selected references if more than one is selected,
		//otherwise over all references in the current filter (or over all, if there is no filter)
		List<Reference> references = Program.ActiveProjectShell.PrimaryMainForm.GetSelectedReferences();
		if (references == null || references.Count <= 1) references = Program.ActiveProjectShell.PrimaryMainForm.GetFilteredReferences();

		List<string> failures = new List<string>();
		Dictionary<Reference, string> pdfPaths = new Dictionary<Reference, string>();

		int completeCounter = 0;

		foreach (Reference reference in references)
		{
			if (HasAbstract(reference) && HasQuotations(reference))
			{
				completeCounter++;	// nothing to add, no need to send the PDF
				continue;
			}

			string reason;
			string pdfPath = GetPdfPath(activeProject, reference, out reason);
			if (pdfPath == null)
			{
				failures.Add(reference.ShortTitle + ": " + reason);
				continue;
			}
			if (new FileInfo(pdfPath).Length > (long)maxPdfMB * 1024 * 1024)
			{
				failures.Add(reference.ShortTitle + ": PDF larger than " + maxPdfMB + " MB");
				continue;
			}
			pdfPaths.Add(reference, pdfPath);
		}

		if (pdfPaths.Count == 0)
		{
			MessageBox.Show("No references to analyze in selection.\r\n" + completeCounter + " reference(s) already have an abstract and quotations.\r\n\r\n" + String.Join("\r\n", failures), "Macro", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		string confirm = String.Format("{0} PDF file(s) will be sent to {1} ({2}) for analysis.\r\nThis may take several minutes per file and incurs API costs.\r\n\r\nDo you want to continue?", pdfPaths.Count, new Uri(apiUrl).Host, model);
		if (MessageBox.Show(confirm, "Citavi", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

		ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;	// TLS 1.2

		Keyword marker = String.IsNullOrWhiteSpace(markerKeyword) ? null : activeProject.Keywords.Add(markerKeyword);
		string logPath = Path.Combine(Path.GetTempPath(), "CCH018_log.txt");

		int processedCounter = 0;
		int abstractCounter = 0;
		int abstractSkippedCounter = 0;
		int quotationCounter = 0;
		int quotationsSkippedCounter = 0;

		foreach (KeyValuePair<Reference, string> entry in pdfPaths)
		{
			Reference reference = entry.Key;
			Dictionary<string, object> result;

			try
			{
				string responseText = CallClaude(apiUrl, apiKey, model, effort, maxQuotations, maxSummaries, entry.Value);
				Dictionary<string, object> response = (Dictionary<string, object>)Json.Parse(responseText);

				string stopReason = response.ContainsKey("stop_reason") ? response["stop_reason"] as string : null;
				if (stopReason == "refusal" || stopReason == "max_tokens")
				{
					failures.Add(reference.ShortTitle + ": request stopped (" + stopReason + ")");
					continue;
				}

				string text = null;
				foreach (object block in (List<object>)response["content"])
				{
					Dictionary<string, object> b = (Dictionary<string, object>)block;
					if (b["type"] as string == "text") text = b["text"] as string;
				}
				if (text == null)
				{
					failures.Add(reference.ShortTitle + ": no text in response");
					File.AppendAllText(logPath, reference.ShortTitle + "\r\n" + responseText + "\r\n\r\n");
					continue;
				}

				try
				{
					result = (Dictionary<string, object>)Json.Parse(text);
				}
				catch (Exception)
				{
					failures.Add(reference.ShortTitle + ": response could not be parsed (see " + logPath + ")");
					File.AppendAllText(logPath, reference.ShortTitle + "\r\n" + text + "\r\n\r\n");
					continue;
				}
			}
			catch (WebException e)
			{
				failures.Add(reference.ShortTitle + ": " + DescribeWebException(e, apiKeyVariable));
				continue;
			}
			catch (Exception e)
			{
				failures.Add(reference.ShortTitle + ": " + e.Message);
				continue;
			}

			processedCounter++;

			//abstract: only if empty
			string newAbstract = result["abstract"] as string;
			if (HasAbstract(reference)) abstractSkippedCounter++;
			else if (!String.IsNullOrWhiteSpace(newAbstract))
			{
				reference.Abstract.Text = newAbstract.Trim();
				abstractCounter++;
			}

			//quotations and summaries: only if the reference has none yet
			if (HasQuotations(reference))
			{
				quotationsSkippedCounter++;
				continue;
			}

			List<KnowledgeItem> quotationsToAdd = new List<KnowledgeItem>();

			foreach (object item in (List<object>)result["quotations"])
			{
				Dictionary<string, object> q = (Dictionary<string, object>)item;
				string pages = FirstNonEmpty(q["printedPage"] as string, NumberToString(q["pdfPageIndex"]));
				KnowledgeItem ki = CreateQuotation(reference, QuotationType.DirectQuotation, q["text"] as string, q["coreStatement"] as string, pages, marker);
				if (ki != null) quotationsToAdd.Add(ki);
			}

			foreach (object item in (List<object>)result["summaries"])
			{
				Dictionary<string, object> s = (Dictionary<string, object>)item;
				string pages = PageRangeString(s["printedPageStart"] as string, s["printedPageEnd"] as string);
				if (pages == "") pages = PageRangeString(NumberToString(s["pdfPageStart"]), NumberToString(s["pdfPageEnd"]));
				KnowledgeItem ki = CreateQuotation(reference, QuotationType.Summary, s["text"] as string, s["coreStatement"] as string, pages, marker);
				if (ki != null) quotationsToAdd.Add(ki);
			}

			if (quotationsToAdd.Count > 0)
			{
				reference.Quotations.AddRange(quotationsToAdd);
				quotationCounter += quotationsToAdd.Count;
			}
		}

		// Message upon completion
		string message = " {0} reference(s) in selection\n {1} reference(s) skipped (abstract and quotations already present)\n {2} reference(s) analyzed\n {3} abstract(s) written\n {4} abstract(s) left unchanged (already present)\n {5} quotation(s) added\n {6} reference(s) without new quotations (quotations already present)\n";
		message = string.Format(message, references.Count, completeCounter, processedCounter, abstractCounter, abstractSkippedCounter, quotationCounter, quotationsSkippedCounter);
		if (failures.Count > 0) message += "\nNot processed:\n" + String.Join("\n", failures);
		MessageBox.Show(message, "Macro", MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	// Find the first linked PDF file of a reference
	// Read a Windows user environment variable (falling back to the process environment); null if not set
	private static string GetEnvironmentSetting(string name)
	{
		string value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
		if (String.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(name);
		if (value != null) value = value.Trim().Trim('"');		// copied values often carry spaces, line breaks or quotes
		return String.IsNullOrWhiteSpace(value) ? null : value;
	}

	// Returns null and sets reason if no usable PDF is found
	private static string GetPdfPath(Project project, Reference reference, out string reason)
	{
		reason = "no PDF attached";

		foreach (Location location in reference.Locations)
		{
			if (location.LocationType != LocationType.ElectronicAddress) continue;

			LinkedResourceType type = location.Address.LinkedResourceType;
			if (type != LinkedResourceType.AttachmentFile &&
				type != LinkedResourceType.AttachmentRemote &&
				type != LinkedResourceType.AbsoluteFileUri &&
				type != LinkedResourceType.RelativeFileUri) continue;		//all others: not applicable

			if (type == LinkedResourceType.AttachmentRemote && location.Address.CachingStatus != CachingStatus.Available)
			{
				reason = "PDF is stored in the cloud and not downloaded yet (open it once in Citavi)";
				continue;
			}

			string path = null;
			try
			{
				path = location.Address.Resolve().LocalPath;
			}
			catch (Exception) { }

			if (type == LinkedResourceType.AttachmentFile && (String.IsNullOrEmpty(path) || !File.Exists(path)))
				path = Path.Combine(project.Addresses.AttachmentsFolderPath, location.Address);

			if (String.IsNullOrEmpty(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

			if (File.Exists(path)) return path;
			reason = "PDF file not found: " + path;
		}
		return null;
	}

	private static bool HasAbstract(Reference reference)
	{
		return !String.IsNullOrWhiteSpace(reference.Abstract.Text);
	}

	// Any quotation, summary, comment etc. counts; highlights and ideas are ignored
	private static bool HasQuotations(Reference reference)
	{
		foreach (KnowledgeItem knowledgeItem in reference.Quotations.ToList())
		{
			if (knowledgeItem.Reference == null) continue; // ignore Ideas
			if (knowledgeItem.QuotationType == QuotationType.Highlight) continue; // ignore highlights
			return true;
		}
		return false;
	}

	// Create a quotation, or return null if there is no text
	private static KnowledgeItem CreateQuotation(Reference reference, QuotationType type, string text, string coreStatement, string pages, Keyword marker)
	{
		if (String.IsNullOrWhiteSpace(text)) return null;

		KnowledgeItem knowledgeItem = new KnowledgeItem(reference, type);
		knowledgeItem.Text = text.Trim();
		if (!String.IsNullOrWhiteSpace(coreStatement)) knowledgeItem.CoreStatement = coreStatement.Trim();
		if (!String.IsNullOrWhiteSpace(pages)) knowledgeItem.PageRange = knowledgeItem.PageRange.Update(pages);
		if (marker != null) knowledgeItem.Keywords.Add(marker);
		return knowledgeItem;
	}

	private static string FirstNonEmpty(string first, string second)
	{
		return String.IsNullOrWhiteSpace(first) ? second : first.Trim();
	}

	private static string PageRangeString(string start, string end)
	{
		start = (start ?? "").Trim();
		end = (end ?? "").Trim();
		if (start == "") return "";
		if (end == "" || end == start) return start;
		return start + "-" + end;
	}

	private static string NumberToString(object value)
	{
		if (value is double) return ((double)value) > 0 ? ((double)value).ToString("0", CultureInfo.InvariantCulture) : "";
		return value as string ?? "";
	}

	// Send the PDF to the Claude Messages API and return the raw response JSON
	private static string CallClaude(string apiUrl, string apiKey, string model, string effort, int maxQuotations, int maxSummaries, string pdfPath)
	{
		string instructions = String.Join("\n", new string[]
		{
			"You are helping a researcher catalogue this paper in a reference manager.",
			"Read the whole document, including scanned pages.",
			"",
			"1. abstract: write an abstract of the paper in English (150-250 words, one paragraph, no heading).",
			"2. quotations: select up to " + maxQuotations + " of the most important, citable passages (key findings, definitions, central claims).",
			"   - text: copy the passage verbatim in its original language, exactly as printed, without surrounding quotation marks. Join words hyphenated across line breaks.",
			"   - coreStatement: a short English headline (max. 12 words) for the passage.",
			"   - printedPage: the page number printed on the page where the passage starts (e.g. \"237\" or \"xii\"); \"\" if no number is printed.",
			"   - pdfPageIndex: the 1-based page index of that page in the PDF file.",
			"3. summaries: summarize up to " + maxSummaries + " main sections of the paper in English (2-5 sentences each).",
			"   - coreStatement: a short English headline for the section.",
			"   - printedPageStart / printedPageEnd: printed page numbers of the section; \"\" if none.",
			"   - pdfPageStart / pdfPageEnd: 1-based PDF page indices of the section.",
			"",
			"Do not invent passages or page numbers. If the document is not a scholarly text, still do your best with what is there."
		});

		Dictionary<string, object> stringType = new Dictionary<string, object> { { "type", "string" } };
		Dictionary<string, object> integerType = new Dictionary<string, object> { { "type", "integer" } };

		Dictionary<string, object> schema = JsonObject(new Dictionary<string, object>
		{
			{ "abstract", stringType },
			{ "quotations", new Dictionary<string, object> { { "type", "array" }, { "items", JsonObject(new Dictionary<string, object>
				{
					{ "text", stringType }, { "coreStatement", stringType }, { "printedPage", stringType }, { "pdfPageIndex", integerType }
				}) } } },
			{ "summaries", new Dictionary<string, object> { { "type", "array" }, { "items", JsonObject(new Dictionary<string, object>
				{
					{ "text", stringType }, { "coreStatement", stringType },
					{ "printedPageStart", stringType }, { "printedPageEnd", stringType },
					{ "pdfPageStart", integerType }, { "pdfPageEnd", integerType }
				}) } } }
		});

		// server-side refusal fallbacks exist only on the Anthropic API; LiteLLM uses "fallbacks" for its own routing
		bool isAnthropic = new Uri(apiUrl).Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

		Dictionary<string, object> body = new Dictionary<string, object>
		{
			{ "model", model },
			{ "max_tokens", 16000 },
			{ "output_config", new Dictionary<string, object>
				{
					{ "effort", effort },
					{ "format", new Dictionary<string, object> { { "type", "json_schema" }, { "schema", schema } } }
				} },
			{ "messages", new List<object>
				{
					new Dictionary<string, object>
					{
						{ "role", "user" },
						{ "content", new List<object>
							{
								new Dictionary<string, object>
								{
									{ "type", "document" },
									{ "source", new Dictionary<string, object>
										{
											{ "type", "base64" },
											{ "media_type", "application/pdf" },
											{ "data", Convert.ToBase64String(File.ReadAllBytes(pdfPath)) }
										} }
								},
								new Dictionary<string, object> { { "type", "text" }, { "text", instructions } }
							} }
					}
				} }
		};

		if (isAnthropic) body.Add("fallbacks", "default");

		byte[] bodyBytes = Encoding.UTF8.GetBytes(Json.Serialize(body));

		HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
		request.Method = "POST";
		request.ContentType = "application/json";
		request.Headers.Add("anthropic-version", "2023-06-01");
		if (isAnthropic)
		{
			request.Headers.Add("x-api-key", apiKey);
			request.Headers.Add("anthropic-beta", "server-side-fallback-2026-07-01");
		}
		else request.Headers.Add("Authorization", "Bearer " + apiKey);
		request.Timeout = 15 * 60 * 1000;
		request.ReadWriteTimeout = 15 * 60 * 1000;
		request.ContentLength = bodyBytes.Length;

		using (Stream stream = request.GetRequestStream())
		{
			stream.Write(bodyBytes, 0, bodyBytes.Length);
		}

		using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
		using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
		{
			return reader.ReadToEnd();
		}
	}

	private static Dictionary<string, object> JsonObject(Dictionary<string, object> properties)
	{
		return new Dictionary<string, object>
		{
			{ "type", "object" },
			{ "properties", properties },
			{ "required", properties.Keys.Cast<object>().ToList() },
			{ "additionalProperties", false }
		};
	}

	private static string DescribeWebException(WebException e, string apiKeyVariable)
	{
		HttpWebResponse response = e.Response as HttpWebResponse;
		if (response == null) return e.Message;

		string detail = "";
		try
		{
			using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
			{
				detail = reader.ReadToEnd();
			}
			Dictionary<string, object> error = (Dictionary<string, object>)Json.Parse(detail);
			detail = ((Dictionary<string, object>)error["error"])["message"].ToString();
		}
		catch (Exception) { }	// not in the expected format: keep the raw text

		if (detail.Length > 300) detail = detail.Substring(0, 300) + "...";

		int status = (int)response.StatusCode;
		string hint = "";
		if (status == 401) hint = " (check " + apiKeyVariable + ")";
		else if (status == 429 || status == 529 || status >= 500) hint = " (try again later)";
		return "HTTP " + status + hint + ": " + detail;
	}

	// Ask whether backup is available
	private static bool IsBackupAvailable()
	{
		string warning = String.Concat("Important: This macro will make irreversible changes to your project.",
			"\r\n\r\n", "Make sure you have a current backup of your project before you run this macro.",
			"\r\n", "If you aren't sure, click Cancel and then, in the main Citavi window, on the File menu, click Create backup.",
			"\r\n\r\n", "Do you want to continue?"
		);

		return (MessageBox.Show(warning, "Citavi", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2) == DialogResult.OK);
	}
}

// Minimal JSON serializer/parser (no external libraries needed in the macro editor)
// Objects: Dictionary<string, object>, arrays: List<object>, numbers: double
public static class Json
{
	public static string Serialize(object value)
	{
		StringBuilder sb = new StringBuilder();
		Write(sb, value);
		return sb.ToString();
	}

	private static void Write(StringBuilder sb, object value)
	{
		if (value == null) sb.Append("null");
		else if (value is string) WriteString(sb, (string)value);
		else if (value is bool) sb.Append((bool)value ? "true" : "false");
		else if (value is int || value is long || value is double) sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
		else if (value is Dictionary<string, object>)
		{
			sb.Append('{');
			bool first = true;
			foreach (KeyValuePair<string, object> pair in (Dictionary<string, object>)value)
			{
				if (!first) sb.Append(',');
				first = false;
				WriteString(sb, pair.Key);
				sb.Append(':');
				Write(sb, pair.Value);
			}
			sb.Append('}');
		}
		else if (value is List<object>)
		{
			sb.Append('[');
			bool first = true;
			foreach (object item in (List<object>)value)
			{
				if (!first) sb.Append(',');
				first = false;
				Write(sb, item);
			}
			sb.Append(']');
		}
		else throw new ArgumentException("Unsupported JSON type: " + value.GetType());
	}

	private static void WriteString(StringBuilder sb, string s)
	{
		sb.Append('"');
		foreach (char c in s)
		{
			switch (c)
			{
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
					else sb.Append(c);
					break;
			}
		}
		sb.Append('"');
	}

	public static object Parse(string json)
	{
		int index = 0;
		object result = ParseValue(json, ref index);
		SkipWhitespace(json, ref index);
		if (index != json.Length) throw new FormatException("Unexpected characters after JSON value");
		return result;
	}

	private static object ParseValue(string json, ref int index)
	{
		SkipWhitespace(json, ref index);
		if (index >= json.Length) throw new FormatException("Unexpected end of JSON");

		char c = json[index];
		if (c == '{')
		{
			Dictionary<string, object> obj = new Dictionary<string, object>();
			index++;
			SkipWhitespace(json, ref index);
			if (json[index] == '}') { index++; return obj; }
			while (true)
			{
				SkipWhitespace(json, ref index);
				string key = ParseString(json, ref index);
				SkipWhitespace(json, ref index);
				Expect(json, ref index, ':');
				obj[key] = ParseValue(json, ref index);
				SkipWhitespace(json, ref index);
				if (json[index] == ',') { index++; continue; }
				Expect(json, ref index, '}');
				return obj;
			}
		}
		if (c == '[')
		{
			List<object> list = new List<object>();
			index++;
			SkipWhitespace(json, ref index);
			if (json[index] == ']') { index++; return list; }
			while (true)
			{
				list.Add(ParseValue(json, ref index));
				SkipWhitespace(json, ref index);
				if (json[index] == ',') { index++; continue; }
				Expect(json, ref index, ']');
				return list;
			}
		}
		if (c == '"') return ParseString(json, ref index);
		if (String.CompareOrdinal(json, index, "true", 0, "true".Length) == 0) { index += 4; return true; }
		if (String.CompareOrdinal(json, index, "false", 0, "false".Length) == 0) { index += 5; return false; }
		if (String.CompareOrdinal(json, index, "null", 0, "null".Length) == 0) { index += 4; return null; }

		int start = index;
		while (index < json.Length && "+-0123456789.eE".IndexOf(json[index]) >= 0) index++;
		if (start == index) throw new FormatException("Unexpected character '" + c + "' in JSON");
		return double.Parse(json.Substring(start, index - start), CultureInfo.InvariantCulture);
	}

	private static string ParseString(string json, ref int index)
	{
		Expect(json, ref index, '"');
		StringBuilder sb = new StringBuilder();
		while (true)
		{
			char c = json[index++];
			if (c == '"') return sb.ToString();
			if (c != '\\') { sb.Append(c); continue; }

			char e = json[index++];
			switch (e)
			{
				case 'n': sb.Append('\n'); break;
				case 'r': sb.Append('\r'); break;
				case 't': sb.Append('\t'); break;
				case 'b': sb.Append('\b'); break;
				case 'f': sb.Append('\f'); break;
				case 'u': sb.Append((char)Convert.ToInt32(json.Substring(index, 4), 16)); index += 4; break;
				default: sb.Append(e); break;	// " \ /
			}
		}
	}

	private static void SkipWhitespace(string json, ref int index)
	{
		while (index < json.Length && Char.IsWhiteSpace(json[index])) index++;
	}

	private static void Expect(string json, ref int index, char expected)
	{
		if (index >= json.Length || json[index] != expected) throw new FormatException("Expected '" + expected + "' in JSON");
		index++;
	}
}
