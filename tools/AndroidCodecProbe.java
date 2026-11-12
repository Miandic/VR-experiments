import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import org.json.JSONArray;
import org.json.JSONObject;

/** Read-only codec capability inventory. No decoder is started or application installed. */
public final class AndroidCodecProbe {
    public static void main(String[] args) throws Exception {
        JSONArray result = new JSONArray();
        int[][] modes = {{1920,1080,60},{2560,1440,60},{3840,2160,60},{3840,2160,30}};
        for (MediaCodecInfo info : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if (info.isEncoder() || info.isAlias()) continue;
            for (String mime : info.getSupportedTypes()) {
                if (!mime.equals("video/avc") && !mime.equals("video/hevc") && !mime.equals("video/av01")) continue;
                JSONObject item = new JSONObject();
                item.put("name", info.getName()).put("mime", mime)
                    .put("hardware", info.isHardwareAccelerated()).put("softwareOnly", info.isSoftwareOnly());
                MediaCodecInfo.CodecCapabilities caps = info.getCapabilitiesForType(mime);
                item.put("lowLatency", caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency));
                MediaCodecInfo.VideoCapabilities video = caps.getVideoCapabilities();
                item.put("widths", video.getSupportedWidths().toString())
                    .put("heights", video.getSupportedHeights().toString())
                    .put("bitrate", video.getBitrateRange().toString())
                    .put("widthAlignment", video.getWidthAlignment()).put("heightAlignment", video.getHeightAlignment());
                JSONArray supported = new JSONArray();
                for (int[] mode : modes) {
                    JSONObject value = new JSONObject().put("width", mode[0]).put("height", mode[1]).put("fps", mode[2]);
                    try { value.put("supported", video.areSizeAndRateSupported(mode[0], mode[1], mode[2])); }
                    catch (IllegalArgumentException error) { value.put("supported", false); }
                    supported.put(value);
                }
                item.put("modes", supported);
                JSONArray points = new JSONArray();
                if (video.getSupportedPerformancePoints() != null)
                    for (MediaCodecInfo.VideoCapabilities.PerformancePoint point : video.getSupportedPerformancePoints())
                        points.put(point.toString());
                item.put("performancePoints", points);
                JSONArray profiles = new JSONArray();
                for (MediaCodecInfo.CodecProfileLevel level : caps.profileLevels)
                    profiles.put(new JSONObject().put("profile", level.profile).put("level", level.level));
                item.put("profiles", profiles);
                result.put(item);
            }
        }
        System.out.println(result.toString(2));
    }
}
