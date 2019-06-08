﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Lynicon.Attributes;
using Lynicon.Collation;
using Lynicon.Extensibility;
using Lynicon.Linq;
using Lynicon.Map;
using Lynicon.Models;
using Lynicon.Relations;
using Lynicon.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Lynicon.Services;

namespace Lynicon.Repositories
{
    /// <summary>
    /// The container for content item in the Content persistenc model
    /// </summary>
    [Table("ContentItems")]
    public class ContentItem : IContentContainer, IBasicAuditable, ICachesSummary, ICachesContent, ICoreMetadata
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(ContentItem));
        
        /// <summary>
        /// The TableName for content items
        /// </summary>
        public static string TableName { get; set; }

        /// <summary>
        /// Get the summary properties of a content type in Content persistence model
        /// </summary>
        /// <param name="t">A content type</param>
        /// <returns>Dictionary of property name against PropertyInfo for the summary properties</returns>
        public static Dictionary<string, PropertyInfo> GetSummaryProperties(Type t)
        {
            Type summaryType = typeof(Summary);
            var wholeProp = t.GetProperties().FirstOrDefault(pi => summaryType.IsAssignableFrom(pi.PropertyType));
            if (wholeProp != null)
                return new Dictionary<string, PropertyInfo> { { "", wholeProp } };
            var summTypeAttr = t.GetCustomAttribute<SummaryTypeAttribute>();
            if (summTypeAttr != null)
                summaryType = summTypeAttr.SummaryType;
            var propMap = t.GetProperties()
                .Select(pi => new { pi, SummaryAttribute = pi.GetCustomAttribute<SummaryAttribute>() })
                .Where(pia => pia.SummaryAttribute != null)
                .ToDictionary(pia => pia.SummaryAttribute.SummaryProperty ?? pia.pi.Name, pia => pia.pi);
            return propMap;
        }

        /// <summary>
        /// Get the summary type of a content type in Content persistence model
        /// </summary>
        /// <param name="t">the content type</param>
        /// <returns>The summary type</returns>
        public static Type GetSummaryType(Type t)
        {
            Type summaryType = typeof(Summary);
            var wholeProp = t.GetProperties().FirstOrDefault(pi => summaryType.IsAssignableFrom(pi.PropertyType));
            if (wholeProp != null)
                return wholeProp.PropertyType;
            else
            {
                var summTypeAttr = t.GetCustomAttribute<SummaryTypeAttribute>();
                if (summTypeAttr != null)
                    return summTypeAttr.SummaryType;
                else
                    return typeof(Summary);
            }
        }

        /// <summary>
        /// Gets a summary from a content typed item if possible
        /// </summary>
        /// <param name="item">the item from which to get summary</param>
        /// <returns>summary of item</returns>
        public static Summary GetSummary(LyniconSystem sys, object item)
        {
            if (item is ContentItem)
                return ((ContentItem)item).GetSummary(sys);
            
            Summary summ = new Summary();
            summ.Type = item.GetType().UnextendedType();
            //if (item is BaseContent && ((BaseContent)item).OriginalRecord != null)
            //    summ = ((BaseContent)item).OriginalRecord.GetSummary();
            //else
            //{
            if (item is ICoreMetadata)
            {
                var meta = (ICoreMetadata)item;
                summ.Id = meta.Identity;
                summ.Url = ContentMap.Instance.GetUrls(new Address(summ.Type, meta.Path)).FirstOrDefault();
                summ.Version = sys.Versions.GetVersion(meta);
                summ.UniqueId = meta.Id;
            }
            else
            {
                summ.Id = null;
                summ.Title = null;

                summ.Url = null;
                summ.Version = null;
                summ.UniqueId = null;
            }
            //}

            var summMap = GetSummaryProperties(item.GetType());
            Summary newSumm;
            if (summMap.ContainsKey(""))
            {
                newSumm = (Summary)summMap[""].GetValue(item);
            }
            else
            {
                var summType = GetSummaryType(item.GetType());
                newSumm = (Summary)Activator.CreateInstance(summType);
                summMap.Do(kvp =>
                    {
                        var summPi = summType.GetProperty(kvp.Key);
                        if (summPi == null)
                            throw new Exception(
                                string.Format("Summary of {0} lacks property {1} marked with SummaryAttribute",
                                        item.GetType().FullName, kvp.Key));

                        summPi.SetValue(newSumm, kvp.Value.GetValue(item));
                    });
            }

            newSumm.Id = summ.Id;
            newSumm.Title = newSumm.Title ?? summ.Title;
            newSumm.Type = summ.Type;
            newSumm.Url = summ.Url;
            newSumm.Version = summ.Version;
            newSumm.UniqueId = null;

            return newSumm;
        }

        /// <summary>
        /// Get a set of JSON serialization settings
        /// </summary>
        /// <param name="type">content type of object to serialize (for error messages)</param>
        /// <param name="id">id of object to serialize (for error messages)</param>
        /// <returns>A JsonSerializerSettings object</returns>
        private static JsonSerializerSettings GetSerializerSettings(Type type, Guid id)
        {
            return new JsonSerializerSettings
                {
                    Error = delegate(object sender, ErrorEventArgs args)
                    {
                        log.Debug("Deserialisation error " + type.FullName + "." + (args.ErrorContext.Path ?? "") + " " + id.ToString() + ": " + args.ErrorContext.Error.Message);
                        args.ErrorContext.Handled = true;
                    },
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    Formatting = Formatting.None,
                    DateFormatHandling = DateFormatHandling.IsoDateFormat,
                    FloatParseHandling = FloatParseHandling.Double,
                    ContractResolver = new IgnoreMetadataContractResolver()
                };
        }

        Guid id = Guid.Empty;
        // properties are marked virtual so that they can work with interfaces in module assemblies
        // when CoreDb is generating a composed type which inherits from this
        [Key]
        public virtual Guid Id
        {
            get { return id; }
            set
            {
                id = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).Id = value;
                if (this.summaryObject != null)
                    summaryObject.UniqueId = value;
            }
        }

        Guid identity;
        /// <summary>
        /// An identifier which is the same across all versions of the same content item but otherwise unique
        /// </summary>
        public virtual Guid Identity
        {
            get { return identity; }
            set
            {
                identity = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).Identity = value;
                if (this.summaryObject != null)
                    summaryObject.Id = value;
            }
        }
        /// <summary>
        /// The data type of the content item
        /// </summary>
        [Required]
        public virtual string DataType { get; set; }

        string path;
        /// <summary>
        /// The address path for the content item
        /// </summary>
        [AddressComponent(UsePath = true)]
        public virtual string Path
        {
            get { return path; }
            set
            {
                path = value;
                if (contentObject is ICoreMetadata)
                    ((ICoreMetadata)contentObject).Path = path;
                if (summaryObject != null)
                    summaryObject.Url = ContentMap.Instance.GetUrl(this);
            }
        }

        // CHECK: Appears to do nothing, given no overridden versions of Path
        //public void FixPath()
        //{
        //    this.path = Path;
        //}

        string summary;
        /// <summary>
        /// The JSON encoded summary properties
        /// </summary>
        public virtual string Summary
        {
            get { return summary; }
            set
            {
                if (summary != value)
                {
                    summary = value;
                    summaryObject = null;
                }
            }
        }

        //public virtual string References { get; set; }

        string content;
        /// <summary>
        /// The JSON encoded content (non-summary) properties
        /// </summary>
        [NotSummarised]
        public virtual string Content
        {
            get { return content; }
            set
            {
                if (content != value)
                {
                    content = value;
                    contentObject = null;
                }
            }
        }

        string title;
        /// <summary>
        /// The Title of the content item
        /// </summary>
        public virtual string Title
        {
            get
            {
                return title;
            }
            set
            {
                title = value;
                if (summaryObject != null)
                    summaryObject.Title = title;
            }
        }

        DateTime created;
        /// <summary>
        /// When the item was created
        /// </summary>
        public virtual DateTime Created
        {
            get { return created; }
            set
            {
                created = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).Created = value;
            }
        }

        string userCreated;
        /// <summary>
        /// The user who created the item
        /// </summary>
        public virtual string UserCreated
        {
            get { return userCreated; }
            set
            {
                userCreated = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).UserCreated = value;
            }
        }

        DateTime updated;
        /// <summary>
        /// When the item was last updated
        /// </summary>
        public virtual DateTime Updated
        {
            get { return updated; }
            set
            {
                updated = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).Updated = value;
            }
        }

        string userUpdated;
        /// <summary>
        /// The user who last updated the item
        /// </summary>
        public virtual string UserUpdated
        {
            get { return userUpdated; }
            set
            {
                userUpdated = value;
                if (this.contentObject is ICoreMetadata)
                    ((ICoreMetadata)this.contentObject).UserUpdated = value;
            }
        }

        private object _contentObject = null;
        private object contentObject
        {
            get
            {
                if (_contentObject != null)
                    TypeExtender.CopyExtensionData(this, _contentObject);
                return _contentObject;
            }
            set { _contentObject = value; }
        }
        private Summary summaryObject = null;

        /// <summary>
        /// Create a new ContentItem container
        /// </summary>
        public ContentItem() : base() { }
        /// <summary>
        /// Create a new ContentItem container for a given content type and path
        /// </summary>
        /// <param name="type">The Content Type</param>
        /// <param name="path">The path</param>
        public ContentItem(Type type, string path) : base()
        {
            Id = Guid.Empty;
            Identity = new Guid();
            Path = path;
            DataType = type.FullName;
        }

        /// <summary>
        /// Filter an IQueryable to contain only listed content types
        /// </summary>
        /// <param name="iq">The original IQueryable</param>
        /// <param name="types">The list of content types</param>
        /// <returns>The IQueryable as filtered</returns>
        public IQueryable OfContentType(IQueryable iq, List<Type> types)
        {
            string[] dataTypes = types.Select(t => t.FullName).ToArray();
            return iq.AsFacade<ContentItem>().WhereIn(ci => ci.DataType, dataTypes);
        }

        /// <summary>
        /// The content type of the item
        /// </summary>
        [JsonIgnore, NotMapped]
        public Type ContentType
        {
            get
            {
                if (ContentTypeHierarchy.GetContentType(this.DataType) == null)
                {
                    throw new Exception("Content type " + this.DataType + " not registered via a route or directly with ContentTypeHierarchy");
                }
                return ContentTypeHierarchy.GetContentType(this.DataType);
            }
        }

        /// <summary>
        /// Get the content of the item as an object
        /// </summary>
        /// <returns>The contained content item</returns>
        public object GetContent(TypeExtender extender)
        {
            Type type = this.ContentType;
            Type extType = extender[type] ?? type;

            if (contentObject != null)
                return contentObject;

            JObject contentJObject = null;
            if (string.IsNullOrEmpty(this.Content))
            {
                contentObject = Activator.CreateInstance(extType);
                if (this.Id != Guid.Empty) // shouldn't normally happen
                    log.WarnFormat("Reading content from contentitem {0} with no content: {1}", this.Id, Environment.StackTrace);
            }
            else
            {
                contentJObject = JObject.Parse(this.Content);
                var sz = JsonSerializer.Create(GetSerializerSettings(type, this.Id));
                contentObject = contentJObject.ToObject(extType, sz);
                if (contentObject == null)
                    contentObject = Activator.CreateInstance(extType);
            }
                

            var summaryMap = GetSummaryProperties(type);
            // Set the summary property(ies) if record field has data
            if (!string.IsNullOrEmpty(this.Summary) && summaryMap.Count > 0)
            {
                var summType = GetSummaryType(type);
                var summ = JsonConvert.DeserializeObject(this.Summary, summType, GetSerializerSettings(type, this.Id));
                if (summaryMap.ContainsKey(""))
                    summaryMap[""].SetValue(contentObject, summ);
                else
                {
                    JToken dummy;
                    // We check contentJObject to see if the summary property existed in the serialization of the
                    // content, in which case we don't use the property from the summary.  This allows us to safely
                    // change the content classes by adding a property to the summary without losing the data in
                    // that property.  Removal of a property from the summary will still cause data loss however.
                    summaryMap.Do(kvp => 
                        {
                            if (kvp.Value.CanWrite
                                && kvp.Value.GetCustomAttribute<JsonIgnoreAttribute>() == null
                                && !contentJObject.TryGetValue(kvp.Value.Name, out dummy))
                            {
                                if (summType.GetProperty(kvp.Key) == null)
                                    throw new Exception("Type " + type.FullName + " has property " + kvp.Key + " incorrectly marked as being on summary type " + summType.FullName);

                                kvp.Value.SetValue(contentObject, summType.GetProperty(kvp.Key).GetValue(summ));
                            }
                        });
                }
            }

            if (this.Title != null)
            {
                if (summaryMap.ContainsKey(""))
                {
                    var summ = summaryMap[""].GetValue(contentObject);
                    // we know it has the property Title as it inherits from Summary
                    summ.GetType().GetProperty("Title").SetValue(summ, this.Title);
                    summaryMap[""].SetValue(contentObject, summ);
                }
                else
                {
                    var titleProp = type.GetProperty("Title");
                    if (summaryMap.ContainsKey("Title"))
                        titleProp = summaryMap["Title"];

                    if (titleProp != null)
                        titleProp.SetValue(contentObject, this.Title);
                }
            }

            // Set up metadata if possible
            if (typeof(ICoreMetadata).IsAssignableFrom(extType))
                TypeExtender.CopyExtensionData(this, contentObject);

            return contentObject;
        }
        /// <summary>
        /// Get the contained content item of type T
        /// </summary>
        /// <typeparam name="T">The type of the content item</typeparam>
        /// <param name="extender">Type extender for creating content type</param>
        /// <returns>The contained content item</returns>
        public T GetContent<T>(TypeExtender extender) where T: class
        {
            return GetContent(extender) as T;
        }

        /// <summary>
        /// Get the summary of the contained content item
        /// </summary>
        /// <returns>The summary</returns>
        public Summary GetSummary(LyniconSystem sys)
        {
            if (summaryObject != null)
            {
                summaryObject.Version = sys.Versions.GetVersion(this);
                return summaryObject;
            }

            var summType = GetSummaryType(ContentType);
            var summMap = GetSummaryProperties(ContentType);
            if (summMap.Count > 0 && !string.IsNullOrEmpty(this.Summary))
            {
                summaryObject = (Summary)JsonConvert.DeserializeObject(this.Summary, summType, GetSerializerSettings(summType, this.Id));
            }
            else
                summaryObject = null;

            if (summaryObject == null)
            {
                summaryObject = new Summary();
                Type contentType = this.ContentType;
                if (typeof(PageContent).IsAssignableFrom(contentType))
                {
                    // The call to GetContent with an empty TypeExtender won't create an extended type but this is no problem
                    PageContent pageContent = this.GetContent<PageContent>(new TypeExtender());
                    summaryObject.Title = pageContent.PageTitle;
                }
            }

            summaryObject.Url = ContentMap.Instance.GetUrl(this);
            summaryObject.Type = ContentType;
            summaryObject.Id = this.Identity;
            summaryObject.Version = sys.Versions.GetVersion(this);
            if (!string.IsNullOrEmpty(this.Title))
                summaryObject.Title = this.Title;
            summaryObject.UniqueId = this.Id;

            return summaryObject;
        }

        /// <summary>
        /// Set the contained content item
        /// </summary>
        /// <param name="value">The content item object</param>
        public void SetContent(LyniconSystem sys, object value)
        {
            if (!(value is ICoreMetadata))
                throw new ArgumentException("Value for ContentItem.SetContent must be ICoreMetadata");

            DataType = value.GetType().UnextendedType().FullName;

            var summMap = GetSummaryProperties(ContentType);
            var summType = GetSummaryType(ContentType);
            
            var sz = JsonSerializer.Create(GetSerializerSettings(value.GetType(), this.Id));
            JObject jObjectContent = JObject.FromObject(value, sz);

            if (summType != typeof(Summary) || summMap.Count > 0)
            {
                summMap.Do(kvp => // key = summary property name, value = content property info
                {
                    jObjectContent.Remove(kvp.Value.Name);
                });

                var summ = GetSummary(sys, value);
                JObject jObjectSummary = JObject.FromObject(summ);
                jObjectSummary.Remove("Url");
                jObjectSummary.Remove("Type");
                jObjectSummary.Remove("Id");
                jObjectSummary.Remove("Version");
                jObjectSummary.Remove("Title");
                jObjectSummary.Remove("UniqueId");

                this.Summary = jObjectSummary.ToString();

                this.Title = summ.Title;
            }

            //this.References = SetReferenceProperties(value);

            this.Content = jObjectContent.ToString();

            this.contentObject = value; // set cached content object
        }

        #region ICachesSummary Members

        /// <summary>
        /// Invalidate the cached summary
        /// </summary>
        public void InvalidateSummary()
        {
            if (Summary == null)
                Summary = JsonConvert.SerializeObject(this.summaryObject); // implicitly clears summaryObject
            else
                this.summaryObject = null;
        }

        public void EnsureSummaryCache(LyniconSystem sys)
        {
            if (summary != null)
            {
                this.GetSummary(sys);
                this.summaryObject = null; // no longer needed: save space
            }
        }

        #endregion

        #region ICachesContent Members

        public void InvalidateContent()
        {
            throw new NotImplementedException();
        }

        public void EnsureContentCache()
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
