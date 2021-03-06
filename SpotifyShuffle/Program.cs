﻿using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static SpotifyAPI.Web.PlaylistRemoveItemsRequest;

namespace SpotifyShuffle
{
    /// <summary>
    /// A program to shuffle an entire Spotify playlist instead of using shuffle play.
    /// </summary>
    class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("# Spotify Shuffle\nBecause Spotify likes to repeat songs on shuffle play...\n\n");
            new Program().AuthoriseAsync().GetAwaiter().GetResult();
        }

        private static readonly string CLIENT_ID = "9ffc2360c76f49cd8d7e0b2ac115a18f";
        private static SpotifyClient Client;
        private static EmbedIOAuthServer Server;

        private static int Tracks = 0;
        private static int Locals = 0;

        /// <summary>
        /// Creates the server and handles the authorisation request
        /// </summary>
        private async Task AuthoriseAsync()
        {
            // create the callback server on localhost:8888, start it and bind the ImplictGrantReceived event to the method
            Server = new EmbedIOAuthServer(new Uri("http://localhost:8888/callback"), 8888);
            await Server.Start();
            Server.ImplictGrantReceived += OnImplicitGrantReceivedAsync;

            // set up our request with the scopes etc, then open the browser for authorisation
            ObtainToken();

            await Task.Delay(-1); // prevents the program from closing
        }

        /// <summary>
        /// Obtain a new Spotify token
        /// </summary>
        private static void ObtainToken()
        {
            var request = new LoginRequest(Server.BaseUri, CLIENT_ID, LoginRequest.ResponseType.Token)
            {
                Scope = new List<string> { Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic, Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative }
            };
            BrowserUtil.Open(request.ToUri());
        }

        /// <summary>
        /// Called when the user has been authorised
        /// </summary>
        private async Task OnImplicitGrantReceivedAsync(object sender, ImplictGrantResponse response)
        {
            // stop the server and create a new client using the token, get the current users id
            await Server.Stop();
            Client = new SpotifyClient(response.AccessToken);
            string user = (await Client.UserProfile.Current()).Id;

            // make sure the users id isnt null/empty for some strange reason
            if (!String.IsNullOrEmpty(user) && !String.IsNullOrWhiteSpace(user))
            {
                while (true)
                {
                    // get the current users playlists and make sure the list isnt null
                    Paging<SimplePlaylist> playlists = await Client.Playlists.GetUsers(user);
                    if (playlists != null && playlists.Items != null)
                    {
                        // list the playlists to the user
                        ListPlaylists(user, playlists);

                        try
                        {
                            // ask the user which playlist we want to shuffle
                            Console.Write("\nEnter ID of playlist to shuffle: ");
                            int playlistId = Convert.ToInt32(Console.ReadLine());
                            Console.Clear();

                            // make sure the playlist id is valid
                            if (playlistId >= 0 && playlistId < playlists.Items.Count)
                            {
                                // start the shuffle procedure and get the playlist uri
                                string playlistUri = playlists.Items[playlistId].Uri.Split(':')[2];

                                // create our empty lists ready to occupy
                                List<PlaylistTrack<IPlayableItem>> allTracks = new List<PlaylistTrack<IPlayableItem>>();
                                List<Item> songs = new List<Item>();
                                List<Item> songsToRemove = new List<Item>();

                                // calculate how many loops of 100 to cycle through the whole playlist, most api calls are limited to 100 tracks
                                int loops = (int)playlists.Items[playlistId].Tracks.Total / 100;
                                int remainder = (int)playlists.Items[playlistId].Tracks.Total % 100;

                                // get all the tracks from the playlist and populate the lists
                                await GetAllTracksAsync(playlistUri, allTracks, loops);
                                PopulateSongLists(allTracks, songs, songsToRemove);

                                // recalculate the loops and remainder of the playlist, some of the tracks may have been invalid
                                loops = Tracks / 100;
                                remainder = Tracks % 100;
                                Log(LogType.Info, "Shuffle", $"Tracks: {Tracks}, Loops: {loops}, Remainder: {remainder}, Local tracks: {Locals}");

                                // do the actual shuffle
                                List<string> shuffled = Shuffle(songs);
                                if (shuffled.Count != songsToRemove.Count) throw new Exception($"For some reason there are not the same amount of songs in each list... Shuffled: {shuffled.Count}, Original: {songsToRemove.Count}");

                                // remove the tracks from the playlist and then add the shuffled list back
                                await RemoveSongsFromPlaylistAsync(playlistUri, songsToRemove, loops);
                                await Task.Delay(100);
                                await AddSongsToPlaylistAsync(playlistUri, shuffled, loops);
                                await Task.Delay(100);

                                // shuffle local tracks
                                await ReorderSongsAsync(playlistUri);

                                Log(LogType.Success, "Shuffle", "Playlist shuffle complete.");
                            }
                            else
                            {
                                Log(LogType.Error, "Playlist", "Invalid playlist ID");
                            }
                        }
                        catch (APIException apiEx)
                        {
                            Log(LogType.Error, apiEx.Response.StatusCode.ToString(), apiEx.Message);
                        }
                        catch (Exception ex)
                        {
                            Log(LogType.Error, ex.Source, ex.Message);
                        }
                    }
                    else
                    {
                        Log(LogType.Error, "Playlist", "No playlists found");
                    }

                    // check how long left of token
                    int timeLeft = response.ExpiresIn - (int)(DateTime.UtcNow - response.CreatedAt).TotalSeconds;

                    // if enough time remains, ask if they want to shuffle another playlist...
                    if (timeLeft > 60)
                    {
                        Console.Write($"\n\nTime left on token: {timeLeft} seconds");
                        Console.Write("\nWould you like to shuffle another playlist? Y/N ");
                        var key = Console.ReadKey();
                        if (!key.Key.Equals(ConsoleKey.Y))
                        {
                            Log(LogType.Info, "Program", "Exitting program...");
                            await Task.Delay(500);
                            Environment.Exit(0);
                        }
                        else
                        {
                            await Task.Delay(500);
                        }
                    }
                    else // else, ask if they want to obtain a new token
                    {
                        Console.Write("\n\nToken expired... Would you like to obtain a new token? Y/N ");
                        var key = Console.ReadKey();
                        if (!key.Key.Equals(ConsoleKey.Y))
                        {
                            Log(LogType.Info, "Program", "Exitting program...");
                            await Task.Delay(500);
                            Environment.Exit(0);
                        }
                        else
                        {
                            Log(LogType.Info, "Program", "Obtaining new token...");
                            await Server.Start();
                            ObtainToken();
                            return;
                        }
                    }
                }
            }
            else
            {
                Log(LogType.Error, "Playlist", "Invalid user id");
            }

            // end the program if we make it to here...
            Environment.Exit(0);
        }

        /// <summary>
        /// Gets the full list of tracks from a playlist
        /// </summary>
        /// <param name="playlistUri">Playlist to retreive the songs from</param>
        /// <param name="allTracks">List to add the songs to</param>
        /// <param name="loops">How many loops are needed to complete the task</param>
        private async Task GetAllTracksAsync(string playlistUri, List<PlaylistTrack<IPlayableItem>> allTracks, int loops)
        {
            Log(LogType.Info, "Shuffle", "Gathering tracks from playlist...");
            for (int i = 0; i <= loops; i++)
            {
                var toAdd = await Client.Playlists.GetItems(playlistUri, new PlaylistGetItemsRequest() { Offset = i * 100 });
                allTracks.AddRange(toAdd.Items);
            }
        }

        /// <summary>
        /// Lists the playlist to the user so they can select which playlist to shuffle
        /// </summary>
        /// <param name="user">The users id</param>
        /// <param name="playlists">The list of playlists</param>
        private void ListPlaylists(string user, Paging<SimplePlaylist> playlists)
        {
            Console.Clear();
            Console.WriteLine($"# List of Playlists [{user}]\n");
            for (int i = 0; i < playlists.Items.Count; i++)
            {
                if (playlists.Items[i].Tracks != null)
                {
                    Console.WriteLine($"[ID: {i,3} ] {playlists.Items[i].Name} ({playlists.Items[i].Tracks.Total} tracks)");
                }
                else Console.WriteLine(playlists.Items[i].Name + " [INVALID]");
            }
        }

        /// <summary>
        /// Populate the songs and songsToRemove lists using allTracks
        /// </summary>
        /// <param name="allTracks">The full list of tracks to add from</param>
        /// <param name="songs">The playlists uri list</param>
        /// <param name="songsToRemove">The list of songs to remove</param>
        /// <returns></returns>
        private void PopulateSongLists(List<PlaylistTrack<IPlayableItem>> allTracks, List<Item> songs, List<Item> songsToRemove)
        {
            Log(LogType.Info, "Shuffle", "Populating lists...");
            int tracks = 0;
            int locals = 0;
            for (int i = allTracks.Count - 1; i >= 0; i--)
            {
                PlaylistTrack<IPlayableItem> track = allTracks[i];
                if (track.Track != null)
                {
                    bool local = false;
                    string uri = String.Empty;

                    switch (track.Track.Type)
                    {
                        case ItemType.Track:
                            FullTrack t = (track.Track as FullTrack);
                            if (t.Uri.ToLower().Contains("local")) local = true;
                            else uri = t.Uri;
                            break;
                        case ItemType.Episode:
                            FullEpisode e = (track.Track as FullEpisode);
                            if (e.Uri.ToLower().Contains("local")) local = true;
                            else uri = e.Uri;
                            break;
                    }
                    if (!local)
                    {
                        songs.Add(new PlaylistRemoveItemsRequest.Item() { Uri = uri });
                        songsToRemove.Add(new PlaylistRemoveItemsRequest.Item() { Uri = uri });
                        tracks++;
                    }
                    else
                    {
                        locals++;
                        Log(LogType.Warning, "Shuffle", "Found a local song. Skipping...");
                    }
                }
                else Log(LogType.Warning, "Shuffle", "Found an unavailable song. Skipping...");
            }

            Tracks = tracks;
            Locals = locals;
        }

        /// <summary>
        /// Shuffle the list of songs into a list of Uris
        /// </summary>
        /// <param name="songs">List of songs to shuffle</param>
        /// <returns>List of strings representing the songs Uris</returns>
        private List<string> Shuffle(List<Item> songs)
        {
            Log(LogType.Info, "Shuffle", "Shuffling the list...");
            List<string> shuffled = new List<string>();

            Random rnd = new Random();

            while (songs.Count > 0)
            {
                int i = rnd.Next(0, songs.Count);
                shuffled.Add(songs[i].Uri);
                songs.RemoveAt(i);
            }

            return shuffled;
        }

        /// <summary>
        /// Remove the songs from the playlist
        /// </summary>
        /// <param name="playlistUri">The playlist to remove from</param>
        /// <param name="songsToRemove">The songs to remove</param>
        /// <param name="loops">How many loops of 100 this will take</param>
        private async Task RemoveSongsFromPlaylistAsync(string playlistUri, List<Item> songsToRemove, int loops)
        {
            Log(LogType.Info, "Shuffle", "Removing songs from playlist...");
            for (int i = 0; i <= loops; i++)
            {
                if (i == loops)
                {
                    if (songsToRemove.Count > 0)
                    {
                        var removeRequest = new PlaylistRemoveItemsRequest
                        {
                            Tracks = songsToRemove
                        };
                        await Client.Playlists.RemoveItems(playlistUri, removeRequest);
                        Log(LogType.Info, "Shuffle", $"Removed {songsToRemove.Count} songs");
                    }
                }
                else
                {
                    List<Item> songsToRemoveThisLoop = songsToRemove.GetRange(0, 100);
                    songsToRemove.RemoveRange(0, 100);

                    var removeRequest = new PlaylistRemoveItemsRequest
                    {
                        Tracks = songsToRemoveThisLoop
                    };
                    await Client.Playlists.RemoveItems(playlistUri, removeRequest);
                    Log(LogType.Info, "Shuffle", "Removed 100 songs");
                }
                await Task.Delay(50);
            }
        }

        /// <summary>
        /// Add songs back to the playlist
        /// </summary>
        /// <param name="playlistUri">The playlist to add the songs to</param>
        /// <param name="songsToAdd">The songs to add</param>
        /// <param name="loops">How many loops of 100 this will take</param>
        private async Task AddSongsToPlaylistAsync(string playlistUri, List<string> songsToAdd, int loops)
        {
            Log(LogType.Info, "Shuffle", "Adding songs back in shuffled order...");
            for (int i = 0; i <= loops; i++)
            {
                if (i == loops)
                {
                    if (songsToAdd.Count > 0)
                    {
                        var addRequest = new PlaylistAddItemsRequest(songsToAdd);
                        await Client.Playlists.AddItems(playlistUri, addRequest);
                        Log(LogType.Info, "Shuffle", $"Added back {songsToAdd.Count} songs");
                    }
                }
                else
                {
                    List<string> songsToAddThisLoop = songsToAdd.GetRange(0, 100);
                    songsToAdd.RemoveRange(0, 100);

                    var addRequest = new PlaylistAddItemsRequest(songsToAddThisLoop);
                    await Client.Playlists.AddItems(playlistUri, addRequest);
                    Log(LogType.Info, "Shuffle", "Added back 100 songs");
                }
                await Task.Delay(50);
            }
        }

        /// <summary>
        /// Reorder the local songs, they should all be at the top of the playlist to begin with
        /// </summary>
        /// <param name="playlistUri">The playlist being shuffled</param>
        private async Task ReorderSongsAsync(string playlistUri)
        {
            Log(LogType.Info, "Shuffle", "Manually shuffling local songs...");
            Random rnd = new Random();
            for (int i = 0; i < Locals; i++)
            {
                var reorderRequest = new PlaylistReorderItemsRequest(0, rnd.Next(1, (Tracks + Locals)));
                await Client.Playlists.ReorderItems(playlistUri, reorderRequest);
                await Task.Delay(50);
            }

            Log(LogType.Info, "Shuffle", $"Shuffled {Locals} local songs manually");
        }

        /// <summary>
        /// Create a console log
        /// </summary>
        /// <param name="logType">Type of console message</param>
        /// <param name="source">The source of the message</param>
        /// <param name="message">The message</param>
        private void Log(LogType logType, string source, string message)
        {
            DateTime time = DateTime.Now;

            switch (logType)
            {
                case LogType.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case LogType.Warning:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case LogType.Info:
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    break;
                case LogType.Success:
                    Console.ForegroundColor = ConsoleColor.Green;
                    break;
            }

            Console.WriteLine($"{time,20} - [{logType,8}] {source,10}: {message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// The different log types
    /// </summary>
    public enum LogType
    {
        Warning, Error, Info, Success
    }
}
