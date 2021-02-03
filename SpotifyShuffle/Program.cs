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
        static void Main(string[] args)
        {
            Console.WriteLine("# Spotify Shuffle\nBecause Spotify likes to repeat songs on shuffle play...\n\n");
            new Program().AuthoriseAsync().GetAwaiter().GetResult();
        }

        private static readonly string CLIENT_ID = "9ffc2360c76f49cd8d7e0b2ac115a18f";
        private static SpotifyClient client;
        private static EmbedIOAuthServer server;

        /// <summary>
        /// Creates the server and handles the authorisation request
        /// </summary>
        private async Task AuthoriseAsync()
        {
            server = new EmbedIOAuthServer(new Uri("http://localhost:8888/callback"), 8888);
            await server.Start();
            server.ImplictGrantReceived += OnImplicitGrantReceivedAsync;

            var request = new LoginRequest(server.BaseUri, CLIENT_ID, LoginRequest.ResponseType.Token)
            {
                Scope = new List<string> { Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic, Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative }
            };
            BrowserUtil.Open(request.ToUri());

            await Task.Delay(-1); // prevents the program from closing
        }

        /// <summary>
        /// Called when the user has been authorised
        /// </summary>
        private async Task OnImplicitGrantReceivedAsync(object sender, ImplictGrantResponse response)
        {
            await server.Stop();
            client = new SpotifyClient(response.AccessToken);
            string user = (await client.UserProfile.Current()).Id;

            if (!String.IsNullOrEmpty(user) && !String.IsNullOrWhiteSpace(user))
            {
                Paging<SimplePlaylist> playlists = await client.Playlists.GetUsers(user);

                if (playlists != null && playlists.Items != null)
                {
                    ListPlaylists(user, playlists);

                    try
                    {
                        Console.WriteLine("\nEnter ID of playlist to shuffle: ");
                        int playlistId = Convert.ToInt32(Console.ReadLine());
                        Console.Clear();
                        if (playlistId >= 0 && playlistId < playlists.Items.Count)
                        {
                            Log(LogType.Info, "Shuffle", "Shuffling, this may take a moment...");

                            string playlistUri = playlists.Items[playlistId].Uri.Split(':')[2];
                            List<PlaylistTrack<IPlayableItem>> allTracks = new List<PlaylistTrack<IPlayableItem>>();
                            List<PlaylistRemoveItemsRequest.Item> songs = new List<PlaylistRemoveItemsRequest.Item>();
                            List<PlaylistRemoveItemsRequest.Item> songsToRemove = new List<PlaylistRemoveItemsRequest.Item>();
                            int loops = (int)playlists.Items[playlistId].Tracks.Total / 100;
                            int remainder = (int)playlists.Items[playlistId].Tracks.Total % 100;

                            await GetAllTracks(playlistUri, allTracks, loops);
                            int tracks = PopulateSongLists(allTracks, songs, songsToRemove);

                            loops = tracks / 100;
                            remainder = tracks % 100;
                            Log(LogType.Info, "Shuffle", $"Tracks: {tracks}, Loops: {loops}, Remainder: {remainder}");

                            Log(LogType.Info, "Shuffle", "Shuffling the list...");
                            List<string> shuffled = Shuffle(songs);
                            if (shuffled.Count != songsToRemove.Count) throw new Exception($"For some reason there are not the same amount of songs in each list... Shuffled: {shuffled.Count}, Original: {songsToRemove.Count}");

                            await RemoveSongsFromPlaylist(playlistUri, songsToRemove, loops);
                            await Task.Delay(100);
                            await AddSongsToPlaylist(playlistUri, shuffled, loops);

                            Log(LogType.Info, "Shuffle", "Playlist shuffle complete.");
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
            }
            else
            {
                Log(LogType.Error, "Playlist", "Invalid playlist ID");
            }


            Console.WriteLine("\n\nPress any key to exit...");
            Console.ReadKey();
            Environment.Exit(0);
        }

        /// <summary>
        /// Gets the full list of tracks from a playlist
        /// </summary>
        /// <param name="playlistUri">Playlist to retreive the songs from</param>
        /// <param name="allTracks">List to add the songs to</param>
        /// <param name="loops">How many loops are needed to complete the task</param>
        private async Task GetAllTracks(string playlistUri, List<PlaylistTrack<IPlayableItem>> allTracks, int loops)
        {
            for (int i = 0; i <= loops; i++)
            {
                var toAdd = await client.Playlists.GetItems(playlistUri, new PlaylistGetItemsRequest() { Offset = i * 100 });
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
        private int PopulateSongLists(List<PlaylistTrack<IPlayableItem>> allTracks, List<Item> songs, List<Item> songsToRemove)
        {
            int tracks = 0;

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
                        Log(LogType.Warning, "Shuffle", "Found a local song. Skipping...");
                    }
                }
                else Log(LogType.Warning, "Shuffle", "Found an unavailable song. Skipping...");
            }

            return tracks;
        }

        /// <summary>
        /// Shuffle the list of songs into a list of Uris
        /// </summary>
        /// <param name="songs">List of songs to shuffle</param>
        /// <returns>List of strings representing the songs Uris</returns>
        private List<string> Shuffle(List<PlaylistRemoveItemsRequest.Item> songs)
        {
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
        private async Task RemoveSongsFromPlaylist(string playlistUri, List<Item> songsToRemove, int loops)
        {
            Log(LogType.Info, "Shuffle", "Removing songs from playlist...");
            for (int i = 0; i <= loops; i++)
            {
                if (i == loops)
                {
                    if (songsToRemove.Count > 0)
                    {
                        var removeRequest = new PlaylistRemoveItemsRequest();
                        removeRequest.Tracks = songsToRemove;
                        await client.Playlists.RemoveItems(playlistUri, removeRequest);
                        Log(LogType.Info, "Shuffle", $"Removed {songsToRemove.Count} songs");
                    }
                }
                else
                {
                    List<PlaylistRemoveItemsRequest.Item> songsToRemoveThisLoop = songsToRemove.GetRange(0, 100);
                    songsToRemove.RemoveRange(0, 100);

                    var removeRequest = new PlaylistRemoveItemsRequest();
                    removeRequest.Tracks = songsToRemoveThisLoop;
                    await client.Playlists.RemoveItems(playlistUri, removeRequest);
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
        private async Task AddSongsToPlaylist(string playlistUri, List<string> songsToAdd, int loops)
        {
            Log(LogType.Info, "Shuffle", "Adding songs back in shuffled order...");
            for (int i = 0; i <= loops; i++)
            {
                if (i == loops)
                {
                    if (songsToAdd.Count > 0)
                    {
                        var addRequest = new PlaylistAddItemsRequest(songsToAdd);
                        await client.Playlists.AddItems(playlistUri, addRequest);
                        Log(LogType.Info, "Shuffle", $"Added back {songsToAdd.Count} songs");
                    }
                }
                else
                {
                    List<string> songsToAddThisLoop = songsToAdd.GetRange(0, 100);
                    songsToAdd.RemoveRange(0, 100);

                    var addRequest = new PlaylistAddItemsRequest(songsToAddThisLoop);
                    await client.Playlists.AddItems(playlistUri, addRequest);
                    Log(LogType.Info, "Shuffle", "Added back 100 songs");
                }
                await Task.Delay(50);
            }
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
                    Console.ForegroundColor = ConsoleColor.Gray;
                    break;
            }

            Console.WriteLine($"{time} - [{logType.ToString(), 8}] {source, 15}: {message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// The different log types
    /// </summary>
    public enum LogType
    {
        Warning, Error, Info
    }
}
