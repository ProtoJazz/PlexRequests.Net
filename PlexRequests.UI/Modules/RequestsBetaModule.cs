#region Copyright
// /************************************************************************
//    Copyright (c) 2016 Jamie Rees
//    File: RequestsModule.cs
//    Created By: Jamie Rees
//   
//    Permission is hereby granted, free of charge, to any person obtaining
//    a copy of this software and associated documentation files (the
//    "Software"), to deal in the Software without restriction, including
//    without limitation the rights to use, copy, modify, merge, publish,
//    distribute, sublicense, and/or sell copies of the Software, and to
//    permit persons to whom the Software is furnished to do so, subject to
//    the following conditions:
//   
//    The above copyright notice and this permission notice shall be
//    included in all copies or substantial portions of the Software.
//   
//    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
//    LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
//    OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
//    WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//  ************************************************************************/
#endregion

using System;
using System.Linq;

using Nancy;
using Nancy.Responses.Negotiation;
using Nancy.Security;

using PlexRequests.Core;
using PlexRequests.Core.SettingModels;
using PlexRequests.Services.Interfaces;
using PlexRequests.Services.Notification;
using PlexRequests.Store;
using PlexRequests.UI.Models;
using PlexRequests.Helpers;
using PlexRequests.UI.Helpers;
using System.Collections.Generic;
using PlexRequests.Api.Interfaces;
using System.Threading.Tasks;

using NLog;

using PlexRequests.Core.Models;
using PlexRequests.Helpers.Analytics;

using Action = PlexRequests.Helpers.Analytics.Action;

namespace PlexRequests.UI.Modules
{
    public class RequestsBetaModule : BaseAuthModule
    {
        public RequestsBetaModule(
            IRequestService service,
            ISettingsService<PlexRequestSettings> prSettings,
            ISettingsService<RequestSettings> requestSettings,
            ISettingsService<PlexSettings> plex,
            INotificationService notify,
            ISettingsService<SonarrSettings> sonarrSettings,
            ISettingsService<SickRageSettings> sickRageSettings,
            ISettingsService<CouchPotatoSettings> cpSettings,
            ICouchPotatoApi cpApi,
            ISonarrApi sonarrApi,
            ISickRageApi sickRageApi,
            ICacheProvider cache,
            IAnalytics an) : base("requestsbeta", prSettings)
        {
            Service = service;
            PrSettings = prSettings;
            PlexSettings = plex;
            NotificationService = notify;
            SonarrSettings = sonarrSettings;
            SickRageSettings = sickRageSettings;
            CpSettings = cpSettings;
            SonarrApi = sonarrApi;
            SickRageApi = sickRageApi;
            CpApi = cpApi;
            Cache = cache;
            Analytics = an;

            Get["/"] = x => LoadRequests();
            Get["/plexrequestsettings", true] = async (x, ct) => await GetPlexRequestSettings();
            Get["/requestsettings", true] = async (x, ct) => await GetRequestSettings();
            Get["/movies", true] = async (x, ct) => await GetMovies();
            Get["/movies/{searchTerm}", true] = async (x, ct) => await GetMovies((string)x.searchTerm);


            // Everything below is not being used in the beta page
            Get["/tvshows", true] = async (c, ct) => await GetTvShows();
            Get["/albums", true] = async (x, ct) => await GetAlbumRequests();
            Post["/delete", true] = async (x, ct) => await DeleteRequest((int)Request.Form.id);
            Post["/reportissue", true] = async (x, ct) => await ReportIssue((int)Request.Form.requestId, (IssueState)(int)Request.Form.issue, null);
            Post["/reportissuecomment", true] = async (x, ct) => await ReportIssue((int)Request.Form.requestId, IssueState.Other, (string)Request.Form.commentArea);

            Post["/clearissues", true] = async (x, ct) => await ClearIssue((int)Request.Form.Id);

            Post["/changeavailability", true] = async (x, ct) => await ChangeRequestAvailability((int)Request.Form.Id, (bool)Request.Form.Available);
        }

        private static Logger Log = LogManager.GetCurrentClassLogger();
        private IRequestService Service { get; }
        private IAnalytics Analytics { get; }
        private INotificationService NotificationService { get; }
        private ISettingsService<PlexRequestSettings> PrSettings { get; }
        private ISettingsService<PlexSettings> PlexSettings { get; }
        private ISettingsService<RequestSettings> RequestSettings { get; }
        private ISettingsService<SonarrSettings> SonarrSettings { get; }
        private ISettingsService<SickRageSettings> SickRageSettings { get; }
        private ISettingsService<CouchPotatoSettings> CpSettings { get; }
        private ISonarrApi SonarrApi { get; }
        private ISickRageApi SickRageApi { get; }
        private ICouchPotatoApi CpApi { get; }
        private ICacheProvider Cache { get; }

        private Negotiator LoadRequests()
        {
            return View["Index"];
        }

        private async Task<Response> GetPlexRequestSettings()
        {
            return Response.AsJson(await PrSettings.GetSettingsAsync());
        }

        private async Task<Response> GetRequestSettings()
        {
            return Response.AsJson(await RequestSettings.GetSettingsAsync());
        }

        private async Task<Response> GetMovies(string searchTerm = null, bool approved = false, bool notApproved = false,
            bool available = false, bool notAvailable = false, bool released = false, bool notReleased = false)
        {
            var dbMovies = await FilterMovies(searchTerm, approved, notApproved, available, notAvailable, released, notReleased);
            var qualities = await GetQualityProfiles();
            var model = MapMoviesToView(dbMovies.ToList(), qualities);

            return Response.AsJson(model);
        }

        private async Task<Response> GetTvShows()
        {
            var settingsTask = PrSettings.GetSettingsAsync();

            var requests = await Service.GetAllAsync();
            requests = requests.Where(x => x.Type == RequestType.TvShow);

            var dbTv = requests;
            var settings = await settingsTask;
            if (settings.UsersCanViewOnlyOwnRequests && !IsAdmin)
            {
                dbTv = dbTv.Where(x => x.UserHasRequested(Username)).ToList();
            }

            IEnumerable<QualityModel> qualities = new List<QualityModel>();
            if (IsAdmin)
            {
                try
                {
                    var sonarrSettings = await SonarrSettings.GetSettingsAsync();
                    if (sonarrSettings.Enabled)
                    {
                        var result = Cache.GetOrSetAsync(CacheKeys.SonarrQualityProfiles, async () =>
                        {
                            return await Task.Run(() => SonarrApi.GetProfiles(sonarrSettings.ApiKey, sonarrSettings.FullUri));
                        });
                        qualities = result.Result.Select(x => new QualityModel { Id = x.id.ToString(), Name = x.name }).ToList();
                    }
                    else
                    {
                        var sickRageSettings = await SickRageSettings.GetSettingsAsync();
                        if (sickRageSettings.Enabled)
                        {
                            qualities = sickRageSettings.Qualities.Select(x => new QualityModel { Id = x.Key, Name = x.Value }).ToList();
                        }
                    }
                }
                catch (Exception e)
                {
                   Log.Info(e);
                }

            }

            var viewModel = dbTv.Select(tv => new RequestViewModel
            {
                ProviderId = tv.ProviderId,
                Type = tv.Type,
                Status = tv.Status,
                ImdbId = tv.ImdbId,
                Id = tv.Id,
                PosterPath = tv.PosterPath,
                ReleaseDate = tv.ReleaseDate,
                ReleaseDateTicks = tv.ReleaseDate.Ticks,
                RequestedDate = tv.RequestedDate,
                RequestedDateTicks = DateTimeHelper.OffsetUTCDateTime(tv.RequestedDate, DateTimeOffset).Ticks,
                Released = DateTime.Now > tv.ReleaseDate,
                Approved = tv.Available || tv.Approved,
                Title = tv.Title,
                Overview = tv.Overview,
                RequestedUsers = IsAdmin ? tv.AllUsers.ToArray() : new string[] { },
                ReleaseYear = tv.ReleaseDate.Year.ToString(),
                Available = tv.Available,
                Admin = IsAdmin,
                IssueId = tv.IssueId,
                TvSeriesRequestType = tv.SeasonsRequested,
                Qualities = qualities.ToArray(),
                Episodes = tv.Episodes.ToArray(),
            }).ToList();

            return Response.AsJson(viewModel);
        }

        private async Task<Response> GetAlbumRequests()
        {
            var settings = PrSettings.GetSettings();
            var dbAlbum = await Service.GetAllAsync();
            dbAlbum = dbAlbum.Where(x => x.Type == RequestType.Album);
            if (settings.UsersCanViewOnlyOwnRequests && !IsAdmin)
            {
                dbAlbum = dbAlbum.Where(x => x.UserHasRequested(Username));
            }

            var viewModel = dbAlbum.Select(album =>
            {
                return new RequestViewModel
                {
                    ProviderId = album.ProviderId,
                    Type = album.Type,
                    Status = album.Status,
                    ImdbId = album.ImdbId,
                    Id = album.Id,
                    PosterPath = album.PosterPath,
                    ReleaseDate = album.ReleaseDate,
                    ReleaseDateTicks = album.ReleaseDate.Ticks,
                    RequestedDate = album.RequestedDate,
                    RequestedDateTicks = DateTimeHelper.OffsetUTCDateTime(album.RequestedDate, DateTimeOffset).Ticks,
                    Released = DateTime.Now > album.ReleaseDate,
                    Approved = album.Available || album.Approved,
                    Title = album.Title,
                    Overview = album.Overview,
                    RequestedUsers = IsAdmin ? album.AllUsers.ToArray() : new string[] { },
                    ReleaseYear = album.ReleaseDate.Year.ToString(),
                    Available = album.Available,
                    Admin = IsAdmin,
                    IssueId = album.IssueId,
                    TvSeriesRequestType = album.SeasonsRequested,
                    MusicBrainzId = album.MusicBrainzId,
                    ArtistName = album.ArtistName

                };
            }).ToList();

            return Response.AsJson(viewModel);
        }

        private async Task<Response> DeleteRequest(int requestid)
        {
            this.RequiresClaims(UserClaims.Admin);
            Analytics.TrackEventAsync(Category.Requests, Action.Delete, "Delete Request", Username, CookieHelper.GetAnalyticClientId(Cookies));

            var currentEntity = await Service.GetAsync(requestid);
            await Service.DeleteRequestAsync(currentEntity);
            return Response.AsJson(new JsonResponseModel { Result = true });
        }

        /// <summary>
        /// Reports the issue.
        /// Comment can be null if the <c>IssueState != Other</c>
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <param name="issue">The issue.</param>
        /// <param name="comment">The comment.</param>
        /// <returns></returns>
        private async Task<Response> ReportIssue(int requestId, IssueState issue, string comment)
        {
            var originalRequest = await Service.GetAsync(requestId);
            if (originalRequest == null)
            {
                return Response.AsJson(new JsonResponseModel { Result = false, Message = "Could not add issue, please try again or contact the administrator!" });
            }
            originalRequest.Issues = issue;
            originalRequest.OtherMessage = !string.IsNullOrEmpty(comment)
                ? $"{Username} - {comment}"
                : string.Empty;


            var result = await Service.UpdateRequestAsync(originalRequest);

            var model = new NotificationModel
            {
                User = Username,
                NotificationType = NotificationType.Issue,
                Title = originalRequest.Title,
                DateTime = DateTime.Now,
                Body = issue == IssueState.Other ? comment : issue.ToString().ToCamelCaseWords()
            };
            await NotificationService.Publish(model);

            return Response.AsJson(result
                ? new JsonResponseModel { Result = true }
                : new JsonResponseModel { Result = false, Message = "Could not add issue, please try again or contact the administrator!" });
        }

        private async Task<Response> ClearIssue(int requestId)
        {
            this.RequiresClaims(UserClaims.Admin);

            var originalRequest = await Service.GetAsync(requestId);
            if (originalRequest == null)
            {
                return Response.AsJson(new JsonResponseModel { Result = false, Message = "Request does not exist to clear it!" });
            }
            originalRequest.Issues = IssueState.None;
            originalRequest.OtherMessage = string.Empty;

            var result = await Service.UpdateRequestAsync(originalRequest);
            return Response.AsJson(result
                                       ? new JsonResponseModel { Result = true }
                                       : new JsonResponseModel { Result = false, Message = "Could not clear issue, please try again or check the logs" });
        }

        private async Task<Response> ChangeRequestAvailability(int requestId, bool available)
        {
            this.RequiresClaims(UserClaims.Admin);
            Analytics.TrackEventAsync(Category.Requests, Action.Update, available ? "Make request available" : "Make request unavailable", Username, CookieHelper.GetAnalyticClientId(Cookies));
            var originalRequest = await Service.GetAsync(requestId);
            if (originalRequest == null)
            {
                return Response.AsJson(new JsonResponseModel { Result = false, Message = "Request does not exist to change the availability!" });
            }

            originalRequest.Available = available;

            var result = await Service.UpdateRequestAsync(originalRequest);
            return Response.AsJson(result
                                       ? new { Result = true, Available = available, Message = string.Empty }
                                       : new { Result = false, Available = false, Message = "Could not update the availability, please try again or check the logs" });
        }

        private List<RequestViewModel> MapMoviesToView(List<RequestedModel> dbMovies, List<QualityModel> qualities)
        {
            return dbMovies.Select(movie => new RequestViewModel
            {
                ProviderId = movie.ProviderId,
                Type = movie.Type,
                Status = movie.Status,
                ImdbId = movie.ImdbId,
                Id = movie.Id,
                PosterPath = movie.PosterPath,
                ReleaseDate = movie.ReleaseDate,
                ReleaseDateTicks = movie.ReleaseDate.Ticks,
                RequestedDate = movie.RequestedDate,
                Released = DateTime.Now > movie.ReleaseDate,
                RequestedDateTicks = DateTimeHelper.OffsetUTCDateTime(movie.RequestedDate, DateTimeOffset).Ticks,
                Approved = movie.Available || movie.Approved,
                Title = movie.Title,
                Overview = movie.Overview,
                RequestedUsers = IsAdmin ? movie.AllUsers.ToArray() : new string[] { },
                ReleaseYear = movie.ReleaseDate.Year.ToString(),
                Available = movie.Available,
                Admin = IsAdmin,
                IssueId = movie.IssueId,
                Qualities = qualities.ToArray()
            }).ToList();
        }

        private async Task<List<QualityModel>> GetQualityProfiles()
        {
            var qualities = new List<QualityModel>();
            if (IsAdmin)
            {
                var cpSettings = CpSettings.GetSettings();
                if (cpSettings.Enabled)
                {
                    try
                    {
                        var result = await Cache.GetOrSetAsync(CacheKeys.CouchPotatoQualityProfiles, async () =>
                        {
                            return await Task.Run(() => CpApi.GetProfiles(cpSettings.FullUri, cpSettings.ApiKey)).ConfigureAwait(false);
                        });
                        if (result != null)
                        {
                            qualities = result.list.Select(x => new QualityModel { Id = x._id, Name = x.label }).ToList();
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Info(e);
                    }
                }
            }
            return qualities;
        }

        private async Task<IEnumerable<RequestedModel>> FilterMovies(string searchTerm = null, bool approved = false, bool notApproved = false,
            bool available = false, bool notAvailable = false, bool released = false, bool notReleased = false)
        {
            var settings = PrSettings.GetSettings();
            var allRequests = await Service.GetAllAsync();
            allRequests = allRequests.Where(x => x.Type == RequestType.Movie);

            var dbMovies = allRequests;

            if (settings.UsersCanViewOnlyOwnRequests && !IsAdmin)
            {
                dbMovies = dbMovies.Where(x => x.UserHasRequested(Username));
            }

            // Filter the movies on the search term
            if (!string.IsNullOrEmpty(searchTerm))
            {
                dbMovies = dbMovies.Where(x => x.Title.Contains(searchTerm));
            }

            if (approved)
            {
                dbMovies = dbMovies.Where(x => x.Approved);
            }

            if (notApproved)
            {
                dbMovies = dbMovies.Where(x => !x.Approved);
            }

            if (available)
            {
                dbMovies = dbMovies.Where(x => x.Available);
            }

            if (notAvailable)
            {
                dbMovies = dbMovies.Where(x => !x.Available);
            }

            if (released)
            {
                dbMovies = dbMovies.Where(x => DateTime.Now > x.ReleaseDate);
            }

            if (notReleased)
            {
                dbMovies = dbMovies.Where(x => DateTime.Now < x.ReleaseDate);
            }

            return dbMovies;
        }
    }
}
