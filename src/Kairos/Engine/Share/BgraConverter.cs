using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>
/// Decoded pictures to BGRA at a size, for the player and the stills, in the colours they were recorded in: swscale
/// assumes BT.601 unless told, and Kairos records BT.709, so reds and greens came out duller than in a real player
/// (pure red as 232, 0, 1). A picture on the graphics card is copied to memory first. One converter is kept while the
/// picture's size, format and colours stay the same; one thread uses it.
/// </summary>
internal sealed unsafe class BgraConverter : IDisposable
{
    SwsContext* _sws;
    /// <summary>A graphics card picture's copy in memory, made on the first such picture (never before FFmpeg is loaded).</summary>
    AVFrame* _transfer;
    (int W, int H, AVPixelFormat Format, int OutW, int OutH, SwsFlags Flags, int Matrix, bool Full) _key;
    readonly byte*[] _srcData = new byte*[4], _dstData = new byte*[4];
    readonly int[] _srcStride = new int[4], _dstStride = new int[4];

    /// <param name="decoded">The decoded frame (on the graphics card or in memory); it says how its colours were recorded.</param>
    public void Convert(AVFrame* decoded, int outW, int outH, SwsFlags flags, byte* dst, int dstStride)
    {
        AVFrame* picture = decoded;
        if (decoded->hw_frames_ctx != null)
        {
            if (_transfer == null)
                _transfer = ffmpeg.av_frame_alloc();
            ClipTranscoder.Check(ffmpeg.av_hwframe_transfer_data(_transfer, decoded, 0), "Reading the decoded picture");
            picture = _transfer;
        }
        try
        {
            int matrix = Matrix(decoded->colorspace, picture->height);
            bool full = decoded->color_range == AVColorRange.AVCOL_RANGE_JPEG;
            var key = (picture->width, picture->height, (AVPixelFormat)picture->format, outW, outH, flags, matrix, full);
            if (_sws == null || _key != key)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = ffmpeg.sws_getContext(picture->width, picture->height, (AVPixelFormat)picture->format, outW, outH,
                    AVPixelFormat.AV_PIX_FMT_BGRA, (int)flags, null, null, null);
                if (_sws == null)
                    throw new ShareException("Cannot convert the picture.");
                _key = key;
                int* c = ffmpeg.sws_getCoefficients(matrix);
                var table = new int_array4();
                table.UpdateFrom([c[0], c[1], c[2], c[3]]);
                // Brightness 0, contrast and saturation 1 (16.16): swscale's own; the picture out is full-range RGB.
                ffmpeg.sws_setColorspaceDetails(_sws, in table, full ? 1 : 0, in table, 1, 0, 1 << 16, 1 << 16);
            }
            for (uint i = 0; i < 4; i++)
            {
                _srcData[i] = picture->data[i];
                _srcStride[i] = picture->linesize[i];
            }
            _dstData[0] = dst;
            _dstStride[0] = dstStride;
            ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, picture->height, _dstData, _dstStride);
        }
        finally
        {
            if (_transfer != null)
                ffmpeg.av_frame_unref(_transfer);
        }
    }

    /// <summary>The YUV matrix: the clip's own, else BT.709 from 720 lines up and BT.601 below (what players assume).</summary>
    internal static int Matrix(AVColorSpace space, int height) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => ffmpeg.SWS_CS_ITU601,
        AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
        AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
        _ => height >= 720 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601,
    };

    public void Dispose()
    {
        if (_sws != null)
            ffmpeg.sws_freeContext(_sws);
        _sws = null;
        if (_transfer != null)
        {
            AVFrame* f = _transfer;
            ffmpeg.av_frame_free(&f);
            _transfer = null;
        }
    }
}
