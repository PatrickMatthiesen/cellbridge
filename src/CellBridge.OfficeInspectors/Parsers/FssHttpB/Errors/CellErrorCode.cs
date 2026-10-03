#nullable disable
// Ported from OfficeDev/Office-Inspectors-for-Fiddler. See NOTICE.md in this project.
//-----------------------------------------------------------------------
// Copyright (c) 2013 Microsoft Corporation. All rights reserved.
// Use of this sample source code is subject to the terms of the Microsoft license
// agreement under which you licensed this sample source code and is provided AS-IS.
// If you did not accept the terms of the license agreement, you are not authorized
// to use this sample source code. For the terms of the license, please see the
// license agreement between you and Microsoft.
//-----------------------------------------------------------------------

namespace CellBridge.OfficeInspectors.Parsers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Xml.Serialization;
    using System.Xml;
    using System.Xml.Schema;
    using System.Reflection;
    using System.Linq;

    /// <summary>
    /// The enumeration of the cell error code. section 2.2.3.2.1
    /// </summary>
    public enum CellErrorCode : uint
    {
        /// <summary>
        /// Unknown error
        /// </summary>
        Unknownerror = 1,

        /// <summary>
        /// Invalid object
        /// </summary>
        InvalidObject = 2,

        /// <summary>
        /// Invalid partition
        /// </summary>
        Invalidpartition = 3,

        /// <summary>
        /// Request not supported
        /// </summary>
        Requestnotsupported = 4,

        /// <summary>
        /// Storage readonly
        /// </summary>
        Storagereadonly = 5,

        /// <summary>
        /// Revision ID not found
        /// </summary>
        RevisionIDnotfound = 6,

        /// <summary>
        /// The Bad token
        /// </summary>
        Badtoken = 7,

        /// <summary>
        /// Request not finished
        /// </summary>
        Requestnotfinished = 8,

        /// <summary>
        /// Incompatible token
        /// </summary>
        Incompatibletoken = 9,

        /// <summary>
        /// Scoped cell storage
        /// </summary>
        Scopedcellstorage = 11,

        /// <summary>
        /// Coherency failure
        /// </summary>
        Coherencyfailure = 12,

        /// <summary>
        /// Cell storage state deserialization failure
        /// </summary>
        Cellstoragestatedeserializationfailure = 13,

        /// <summary>
        /// Incompatible protocol version
        /// </summary>
        Incompatibleprotocolversion = 15,

        /// <summary>
        /// Referenced data element not found
        /// </summary>
        Referenceddataelementnotfound = 16,

        /// <summary>
        /// Request stream schema error
        /// </summary>
        Requeststreamschemaerror = 18,

        /// <summary>
        /// Response stream schema error
        /// </summary>
        Responsestreamschemaerror = 19,

        /// <summary>
        /// Unknown request
        /// </summary>
        Unknownrequest = 20,

        /// <summary>
        /// Storage failure
        /// </summary>
        Storagefailure = 21,

        /// <summary>
        /// Storage write only
        /// </summary>
        Storagewriteonly = 22,

        /// <summary>
        /// Invalid serialization
        /// </summary>
        Invalidserialization = 23,

        /// <summary>
        /// Data element not found
        /// </summary>
        Dataelementnotfound = 24,

        /// <summary>
        /// Invalid implementation
        /// </summary>
        Invalidimplementation = 25,

        /// <summary>
        /// Incompatible old storage
        /// </summary>
        Incompatibleoldstorage = 26,

        /// <summary>
        /// Incompatible new storage
        /// </summary>
        Incompatiblenewstorage = 27,

        /// <summary>
        /// Incorrect context for data element ID
        /// </summary>
        IncorrectcontextfordataelementID = 28,

        /// <summary>
        /// Object group duplicate objects
        /// </summary>
        Objectgroupduplicateobjects = 29,

        /// <summary>
        /// Object reference not founding revision
        /// </summary>
        Objectreferencenotfoundinrevision = 31,

        /// <summary>
        /// Merge cell storage state conflict
        /// </summary>
        Mergecellstoragestateconflict = 32,

        /// <summary>
        /// Unknown query changes filter
        /// </summary>
        Unknownquerychangesfilter = 33,

        /// <summary>
        /// Unsupported query changes filter
        /// </summary>
        Unsupportedquerychangesfilter = 34,

        /// <summary>
        /// Unable to provide knowledge
        /// </summary>
        Unabletoprovideknowledge = 35,

        /// <summary>
        /// Data element missing ID
        /// </summary>
        DataelementmissingID = 36,

        /// <summary>
        /// Data element missing serial number
        /// </summary>
        Dataelementmissingserialnumber = 37,

        /// <summary>
        /// Request argument invalid
        /// </summary>
        Requestargumentinvalid = 38,

        /// <summary>
        /// Partial changes not supported
        /// </summary>
        Partialchangesnotsupported = 39,

        /// <summary>
        /// Store busy retry later
        /// </summary>
        Storebusyretrylater = 40,

        /// <summary>
        /// GUIDID table not supported
        /// </summary>
        GUIDIDtablenotsupported = 41,

        /// <summary>
        /// Data element cycle
        /// </summary>
        Dataelementcycle = 42,

        /// <summary>
        /// Fragment knowledge error
        /// </summary>
        Fragmentknowledgeerror = 43,

        /// <summary>
        /// Fragment size mismatch
        /// </summary>
        Fragmentsizemismatch = 44,

        /// <summary>
        /// Fragments incomplete
        /// </summary>
        Fragmentsincomplete = 45,

        /// <summary>
        /// Fragment invalid
        /// </summary>
        Fragmentinvalid = 46,

        /// <summary>
        /// Aborted after failed put changes
        /// </summary>
        Abortedafterfailedputchanges = 47,

        /// <summary>
        /// Upgrade failed because there are no upgradeable contents.
        /// </summary>
        FailedNoUpgradeableContents = 79,

        /// <summary>
        /// Unable to allocate additional extended GUIDs.
        /// </summary>
        UnableAllocateAdditionalExtendedGuids = 106,

        /// <summary>
        /// Site is in read-only mode.
        /// </summary>
        SiteReadonlyMode = 108,

        /// <summary>
        /// Multi-Request partition reached quota.
        /// </summary>
        MultiRequestPartitionReachQutoa = 111,

        /// <summary>
        /// Extended GUID collision.
        /// </summary>
        ExtendedGuidCollision = 112,

        /// <summary>
        /// Upgrade failed because of insufficient permissions.
        /// </summary>
        InsufficientPermisssions = 113,

        /// <summary>
        /// Upgrade failed because of server throttling.
        /// </summary>
        ServerThrottling = 114,

        /// <summary>
        /// Upgrade failed because the upgraded file is too large.
        /// </summary>
        FileTooLarge = 115
    }
}
