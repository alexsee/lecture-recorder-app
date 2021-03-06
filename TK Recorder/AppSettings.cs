﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;

namespace TK_Recorder
{
    public struct AppSettings
    {
        public VideoEncodingQuality Quality;

        public uint FrameRate;

        public bool UseSourceSize;

        public bool AdaptBitrate;

        public string StorageFolder;

        public string AudioDeviceId;

        public string WebcamDeviceId;

        public string WebcamQuality;

        public double WebcamExposure;

        public bool WebcamExposureAuto;

        public double WebcamWhiteBalance;

        public bool WebcamWhiteBalanceAuto;

        public string BackendApiUrl;
    }
}
