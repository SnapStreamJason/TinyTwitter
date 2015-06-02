using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TinyTwitter
{
	public class OAuthInfo
	{
		public string ConsumerKey { get; set; }
		public string ConsumerSecret { get; set; }
		public string AccessToken { get; set; }
		public string AccessSecret { get; set; }
	}

	public class Tweet
	{
		public long Id { get; set; }
		public DateTime CreatedAt { get; set; }
		public string UserName { get; set; }
		public string ScreenName { get; set; }
		public string Text { get; set; }
	}

	public class ChunkedInitResult 
	{
		public long media_id { get; set; }
		public string media_id_string { get; set; }
		public int expires_after_secs { get; set; }
	}

	public class TinyTwitter
	{
		private readonly OAuthInfo oauth;

		public TinyTwitter(OAuthInfo oauth)
		{
			this.oauth = oauth;
		}

		public void UpdateStatus(string message)
		{
			new RequestBuilder(oauth, "POST", "https://api.twitter.com/1.1/statuses/update.json")
				.AddParameter("status", message)
				.Execute();
		}

		public IEnumerable<Tweet> GetHomeTimeline(long? sinceId = null, long? maxId = null, int? count = 20)
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/home_timeline.json", sinceId, maxId, count, "");
		}

		public IEnumerable<Tweet> GetMentions(long? sinceId = null, long? maxId = null, int? count = 20)
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/mentions.json", sinceId, maxId, count, "");
		}

		public IEnumerable<Tweet> GetUserTimeline(long? sinceId = null, long? maxId = null, int? count = 20, string screenName = "")
		{
			return GetTimeline("https://api.twitter.com/1.1/statuses/user_timeline.json", sinceId, maxId, count, screenName);
		}

		public string UpdateStatusWithMedia( string message, string media ) 
		{
			//You can get a "The validation of media ids failed." error here when there was something wrong with the video encode
			//Upload succeeds, but then the actual status update fails. I believe there is an unstated MOOV before MDAT requirement on the files.
			string web = new RequestBuilder( oauth, "POST", "https://api.twitter.com/1.1/statuses/update.json" )
				.AddParameter( "status", message )
				.AddParameter( "media_ids", media )
				.Execute();
			return web;
		}

		public string UploadMedia( Stream file, string mediaType ) 
		{

			// Make the initial request, this should get us the id we want to use
			string response = new RequestBuilder( oauth, "POST", "https://upload.twitter.com/1.1/media/upload.json" )
				.AddParameter( "command", "INIT" )
				.AddParameter( "media_type", mediaType )
				.AddParameter( "total_bytes", file.Length.ToString() )
				.Execute();

			var serializer = new JavaScriptSerializer();
			var initResult = serializer.Deserialize<ChunkedInitResult>( response );

			long pos = 0;
			long totalBytes = file.Length;
			int segment = 0;

			while( pos < totalBytes ) {
				byte[] bytes = new byte[ Math.Min( 1 * 1024 * 1024, totalBytes - pos ) ];

				int bytesToRead = bytes.Length;
				int totalBytesRead = 0;

				while( totalBytesRead < bytesToRead ) {
					int bytesRead = file.Read( bytes, totalBytesRead, bytesToRead - totalBytesRead );

					if( bytesRead == 0 ) {
						throw new Exception( "Read 0 bytes!" );
					}

					totalBytesRead += bytesRead;
				}

				response = new RequestBuilder( oauth, "POST", "https://upload.twitter.com/1.1/media/upload.json" )
					.AddParameter( "command", "APPEND" )
					.AddParameter( "media_id", initResult.media_id_string )
					.AddParameter( "segment_index", segment.ToString() )
					.MultipartExecute( bytes );


				/*
				 * Documentation doesn't mention this, but it seems to work. There's a Non multipart way
				 * to upload a chunk. In this case all the parameters are used in the signature, not just the oauth_* ones
				 * 	web = new RequestBuilder( oauth, "POST", "https://upload.twitter.com/1.1/media/upload.json" )
						.AddParameter( "command", "APPEND" )
						.AddParameter( "media_id", initResult.media_id_string )
						.AddParameter( "segment_index", segment.ToString() )
						.AddParameter( "media_data", System.Convert.ToBase64String( bytes ) )
						.Execute();
				 * */

				segment++;
				pos += bytes.Length;
			}

			response = new RequestBuilder( oauth, "POST", "https://upload.twitter.com/1.1/media/upload.json" )
				.AddParameter( "command", "FINALIZE" )
				.AddParameter( "media_id", initResult.media_id_string )
				.Execute();

			return initResult.media_id_string;
		}

		private IEnumerable<Tweet> GetTimeline(string url, long? sinceId, long? maxId, int? count, string screenName)
		{
			var builder = new RequestBuilder(oauth, "GET", url);

			if (sinceId.HasValue)
				builder.AddParameter("since_id", sinceId.Value.ToString());

			if (maxId.HasValue)
				builder.AddParameter("max_id", maxId.Value.ToString());

			if (count.HasValue)
				builder.AddParameter("count", count.Value.ToString());

			if (screenName != "")
				builder.AddParameter("screen_name", screenName);

			var responseContent = builder.Execute();

			var serializer = new JavaScriptSerializer();

			var tweets = (object[])serializer.DeserializeObject(responseContent);

			return tweets.Cast<Dictionary<string, object>>().Select(tweet =>
			{
				var user = ((Dictionary<string, object>)tweet["user"]);
				var date = DateTime.ParseExact(tweet["created_at"].ToString(),
					"ddd MMM dd HH:mm:ss zz00 yyyy",
					CultureInfo.InvariantCulture).ToLocalTime();

				return new Tweet
				{
					Id = (long)tweet["id"],
					CreatedAt = date,
					Text = (string)tweet["text"],
					UserName = (string)user["name"],
					ScreenName = (string)user["screen_name"]
				};
			}).ToArray();
		}

		#region RequestBuilder

		public class RequestBuilder
		{
			private const string VERSION = "1.0";
			private const string SIGNATURE_METHOD = "HMAC-SHA1";

			private readonly OAuthInfo oauth;
			private readonly string method;
			private readonly IDictionary<string, string> customParameters;
			private readonly string url;

			public RequestBuilder(OAuthInfo oauth, string method, string url)
			{
				this.oauth = oauth;
				this.method = method;
				this.url = url;
				customParameters = new Dictionary<string, string>();
			}

			public RequestBuilder AddParameter(string name, string value)
			{
				customParameters.Add(name, value.EncodeRFC3986());
				return this;
			}

			public string Execute()
			{
				var timespan = GetTimestamp();
				var nonce = CreateNonce();

				var parameters = new Dictionary<string, string>(customParameters);
				AddOAuthParameters(parameters, timespan, nonce);

				var signature = GenerateSignature(parameters);
				var headerValue = GenerateAuthorizationHeaderValue(parameters, signature);

				var request = (HttpWebRequest)WebRequest.Create(GetRequestUrl());
				request.Method = method;
				request.ContentType = "application/x-www-form-urlencoded";

				request.Headers.Add("Authorization", headerValue);

				WriteRequestBody(request);

				string content;

				WebResponse response = null;

				try {
					response = request.GetResponse();
					using( var stream = response.GetResponseStream() ) {
						using( var reader = new StreamReader( stream ) ) {
							content = reader.ReadToEnd();
						}
					}
				}
				// Useful for debugging
				/*catch( WebException ex ) {

					using( var stream = ex.Response.GetResponseStream() ) {
						using( var reader = new StreamReader( stream ) ) {
							content = reader.ReadToEnd();
						}
					}
					throw;
				}*/
				finally {
					if( response != null ) {
						( (IDisposable)response ).Dispose();
					}
				}

				return content;
			}

			public string MultipartExecute( byte[] bytes )
			{
				var timespan = GetTimestamp();
				var nonce = CreateNonce();

				var parameters = new Dictionary<string, string>();
				AddOAuthParameters( parameters, timespan, nonce );

				// for multi-part requests, the signature is only generated over the oauth_ parameters
				var signature = GenerateSignature( parameters );
				var headerValue = GenerateAuthorizationHeaderValue( parameters, signature );

				var request = (HttpWebRequest)WebRequest.Create( GetRequestUrl() );
				request.Method = method;
				string boundary = "----------" + DateTime.Now.Ticks.ToString( "x" );
				request.ContentType = "multipart/form-data; boundary=" + boundary;

				request.Headers.Add( "Authorization", headerValue );

				WriteMultipartRequestBody( request.GetRequestStream(), boundary, customParameters, bytes );

				string content;

				WebResponse response = null;

				try {
					response = request.GetResponse();
					using( var stream = response.GetResponseStream() ) {
						using( var reader = new StreamReader( stream ) ) {
							content = reader.ReadToEnd();
						}
					}
				}
				// Useful for debugging
				/*catch( WebException ex ) {

					using( var stream = ex.Response.GetResponseStream() ) {
						using( var reader = new StreamReader( stream ) ) {
							content = reader.ReadToEnd();
						}
					}
					throw;
				}*/
				finally {
					if( response != null ) {
						( (IDisposable)response ).Dispose();
					}
				}

				return content;
			}

			private void WriteRequestBody(HttpWebRequest request)
			{
				if (method == "GET")
					return;

				var requestBody = Encoding.ASCII.GetBytes(GetCustomParametersString());
				using (var stream = request.GetRequestStream())
					stream.Write(requestBody, 0, requestBody.Length);
			}

			private string GetRequestUrl()
			{
				if (method != "GET" || customParameters.Count == 0)
					return url;

				return string.Format("{0}?{1}", url, GetCustomParametersString());
			}

			private string GetCustomParametersString()
			{
				return customParameters.Select(x => string.Format("{0}={1}", x.Key, x.Value)).Join("&");
			}

			private string GenerateAuthorizationHeaderValue(IEnumerable<KeyValuePair<string, string>> parameters, string signature)
			{
				return new StringBuilder("OAuth ")
					.Append(parameters.Concat(new KeyValuePair<string, string>("oauth_signature", signature))
								.Where(x => x.Key.StartsWith("oauth_"))
								.Select(x => string.Format("{0}=\"{1}\"", x.Key, x.Value.EncodeRFC3986()))
								.Join(","))
					.ToString();
			}

			private string GenerateSignature(IEnumerable<KeyValuePair<string, string>> parameters)
			{
				var dataToSign = new StringBuilder()
					.Append(method).Append("&")
					.Append(url.EncodeRFC3986()).Append("&")
					.Append(parameters
								.OrderBy(x => x.Key)
								.Select(x => string.Format("{0}={1}", x.Key, x.Value))
								.Join("&")
								.EncodeRFC3986());

				var signatureKey = string.Format("{0}&{1}", oauth.ConsumerSecret.EncodeRFC3986(), oauth.AccessSecret.EncodeRFC3986());
				var sha1 = new HMACSHA1(Encoding.ASCII.GetBytes(signatureKey));

				var signatureBytes = sha1.ComputeHash(Encoding.ASCII.GetBytes(dataToSign.ToString()));
				return Convert.ToBase64String(signatureBytes);
			}

			private void AddOAuthParameters(IDictionary<string, string> parameters, string timestamp, string nonce)
			{
				parameters.Add("oauth_version", VERSION);
				parameters.Add("oauth_consumer_key", oauth.ConsumerKey);
				parameters.Add("oauth_nonce", nonce);
				parameters.Add("oauth_signature_method", SIGNATURE_METHOD);
				parameters.Add("oauth_timestamp", timestamp);
				parameters.Add("oauth_token", oauth.AccessToken);
			}

			private static string GetTimestamp()
			{
				return ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
			}

			private static string CreateNonce()
			{
				return new Random().Next(0x0000000, 0x7fffffff).ToString("X8");
			}

			private static void WriteMultipartRequestBody( Stream requestStream, string boundary, IDictionary<string, string> parameters, byte[] fileData ) {
				byte[]	boundarybytes = Encoding.ASCII.GetBytes( "--" + boundary + "\r\n" );
				byte[]	trailer = Encoding.ASCII.GetBytes( "\r\n--" + boundary + "–-\r\n" );
				byte[]	temp = null;

				if( parameters != null ) {
					foreach( string key in parameters.Keys ) {

						requestStream.Write( boundarybytes, 0, boundarybytes.Length );
						temp = Encoding.ASCII.GetBytes( string.Format( "Content-Disposition: form-data; name=\"{0}\"\r\n\r\n{1}", key, parameters[ key ] ) );
						requestStream.Write( temp, 0, temp.Length );
						temp = Encoding.ASCII.GetBytes( "\r\n" );
						requestStream.Write( temp, 0, temp.Length );
					}
				}

				//Getting this right is tricky, multipart requests will just end up with "media not found" errors when not done properly
				requestStream.Write( boundarybytes, 0, boundarybytes.Length );
				temp = Encoding.ASCII.GetBytes( string.Format( "Content-Disposition: form-data; name=\"{0}\"\r\n", "media" ) );
				requestStream.Write( temp, 0, temp.Length );

				// documentation says this is required, but I've seen it work just fine without it
				temp = Encoding.ASCII.GetBytes( "Content-Type: application/octect-stream\r\n\r\n" );
				requestStream.Write( temp, 0, temp.Length );

				requestStream.Write( fileData, 0, fileData.Length );
				requestStream.Write( trailer, 0, trailer.Length );
			}
		}

		#endregion
	}

	public static class TinyTwitterHelperExtensions
	{
		public static string Join<T>(this IEnumerable<T> items, string separator)
		{
			return string.Join(separator, items.ToArray());
		}

		public static IEnumerable<T> Concat<T>(this IEnumerable<T> items, T value)
		{
			return items.Concat(new[] { value });
		}

		public static string EncodeRFC3986(this string value)
		{
			// From Twitterizer http://www.twitterizer.net/

			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}

			int limit = 20000;

			//Handle very large strings, Uri.EscapeDataString has a max length of 32766
			// so we'll look at this in chunks and append it all together
			StringBuilder sb = new StringBuilder(value.Length);
			int loops = value.Length / limit;

			for(int i = 0; i <= loops; i++)
			{
				if(i < loops)
				{
					sb.Append(Uri.EscapeDataString(value.Substring(limit * i, limit)));
				}
				else
				{
					sb.Append(Uri.EscapeDataString(value.Substring(limit * i)));
				}
			}

			var encoded = sb.ToString();

			return Regex
				.Replace(encoded, "(%[0-9a-f][0-9a-f])", c => c.Value.ToUpper())
				.Replace("(", "%28")
				.Replace(")", "%29")
				.Replace("$", "%24")
				.Replace("!", "%21")
				.Replace("*", "%2A")
				.Replace("'", "%27")
				.Replace("%7E", "~");
		}
	}
}
