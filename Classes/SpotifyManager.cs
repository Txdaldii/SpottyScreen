using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SpottyScreen.Classes
{
    internal class SpotifyManager
    {
        private MainWindow mainWindow;

        public SpotifyManager(MainWindow mainWindow)
        {
            this.mainWindow = mainWindow;
        }

        public async void Authenticate()
        {
            const string redirectUri = "http://127.0.0.1:5000/callback";
            const string clientId = "41033dc65baf42e287b21398aafb4501";

            string savedAccessToken = Properties.Settings.Default.SpotifyAccessToken;
            string savedRefreshToken = Properties.Settings.Default.SpotifyRefreshToken;

            var oauth = new OAuthClient();

            if (!string.IsNullOrEmpty(savedAccessToken) && !string.IsNullOrEmpty(savedRefreshToken))
            {
                mainWindow.Spotify = new SpotifyClient(savedAccessToken);

                try
                {
                    await mainWindow.Spotify.Player.GetCurrentPlayback(); // Test if token works
                    mainWindow.StartPolling();
                    return;
                }
                catch (APIUnauthorizedException)
                {
                    var success = await RefreshAccessToken(clientId, savedRefreshToken, oauth);
                    if (success)
                    {
                        mainWindow.StartPolling();
                        return;
                    }
                }
                catch (Exception ex) // Catch other potential exceptions during initial check
                {
                    Console.WriteLine($"Error initializing Spotify client: {ex.Message}");
                    // Decide how to handle this - maybe attempt full auth flow
                }
            }

            // If token invalid, expired, or refresh failed, start full auth
            await StartAuthorizationCodeFlow(clientId, redirectUri, oauth);
        }

        // Extracted auth flow logic for clarity
        public async Task StartAuthorizationCodeFlow(string clientId, string redirectUri, OAuthClient oauth)
        {
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes();

                var loginRequest = new LoginRequest(
                    new Uri(redirectUri),
                    clientId,
                    LoginRequest.ResponseType.Code
                )
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] { Scopes.UserReadPlaybackState, Scopes.UserReadCurrentlyPlaying, Scopes.UserReadPlaybackPosition }
                };

                using (var http = new HttpListener())
                {
                    http.Prefixes.Add("http://127.0.0.1:5000/callback/"); // Ensure trailing slash
                    http.Start();
                    mainWindow.WindowState = WindowState.Minimized;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = loginRequest.ToUri().ToString(),
                        UseShellExecute = true
                    });

                    var context = await http.GetContextAsync();
                    var code = context.Request.QueryString["code"];

                    // Send response to browser before proceeding
                    string responseHtml = "<html><head><style>body { font-family: sans-serif; background-color: #f0f0f0; text-align: center; padding-top: 50px; }</style></head><body><h1>Authentication Successful!</h1><p>You can now close this window and return to SpottyScreen.</p><script>window.close();</script></body></html>";
                    byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                    context.Response.ContentType = "text/html";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.Close(); // Close the response stream
                    http.Stop(); // Stop the listener
                    mainWindow.WindowState = WindowState.Maximized;

                    if (string.IsNullOrEmpty(code))
                    {
                        MessageBox.Show("Authentication failed: No code received from Spotify.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        // Handle failed auth (e.g., close app, show error message)
                        return;
                    }

                    var tokenRequest = new PKCETokenRequest(clientId, code, new Uri(redirectUri), verifier);
                    var tokenResponse = await oauth.RequestToken(tokenRequest);

                    Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                    Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                    Properties.Settings.Default.Save();

                    mainWindow.Spotify = new SpotifyClient(tokenResponse.AccessToken);
                    mainWindow.StartPolling(); // Start polling after successful auth
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Authentication flow error: {ex.Message}");
                MessageBox.Show($"Authentication failed: {ex.Message}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Consider cleaning up tokens if auth fails definitively
                Properties.Settings.Default.SpotifyAccessToken = null;
                Properties.Settings.Default.SpotifyRefreshToken = null;
                Properties.Settings.Default.Save();
            }
        }

        public async Task<bool> RefreshAccessToken(string clientId, string refreshToken, OAuthClient oauth)
        {
            // Prevent null refresh token issue
            if (string.IsNullOrEmpty(refreshToken))
            {
                Console.WriteLine("Cannot refresh token: Refresh token is missing.");
                MessageBox.Show("Spotify session expired or invalid. Please log in again.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Properties.Settings.Default.SpotifyAccessToken = null; // Clear invalid tokens
                Properties.Settings.Default.SpotifyRefreshToken = null;
                Properties.Settings.Default.Save();
                // Trigger full re-authentication
                await StartAuthorizationCodeFlow(clientId, "http://127.0.0.1:5000/callback", oauth);
                return false; // Indicate refresh wasn't successful (new auth started)
            }

            try
            {
                var refreshRequest = new PKCETokenRefreshRequest(clientId, refreshToken);
                var tokenResponse = await oauth.RequestToken(refreshRequest);

                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;

                // Spotify might issue a new refresh token during refresh
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                {
                    Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                }

                Properties.Settings.Default.Save();

                mainWindow.Spotify = new SpotifyClient(tokenResponse.AccessToken); // Update client with new token
                Console.WriteLine("Access token refreshed successfully.");
                return true;
            }
            catch (APIException apiEx) // Catch specific API errors
            {
                Console.WriteLine($"Token refresh failed: {apiEx.Message}");
                // Handle specific errors, e.g., invalid_grant often means refresh token is revoked/expired
                if (apiEx.Response?.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    MessageBox.Show("Spotify session expired. Please log in again.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Properties.Settings.Default.SpotifyAccessToken = null; // Clear invalid tokens
                    Properties.Settings.Default.SpotifyRefreshToken = null;
                    Properties.Settings.Default.Save();
                    // Trigger full re-authentication
                    await StartAuthorizationCodeFlow(clientId, "http://127.0.0.1:5000/callback", oauth);
                }
                else
                {
                    MessageBox.Show($"Failed to refresh Spotify session: {apiEx.Message}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }
            catch (Exception ex) // Catch other unexpected errors
            {
                Console.WriteLine($"Unexpected error during token refresh: {ex.Message}");
                MessageBox.Show($"An unexpected error occurred while refreshing the Spotify session: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Potentially clear tokens or attempt re-auth depending on the error
                return false;
            }
        }
    }
}
