package android.util;
// JVM test adapter only. Android APK uses Android's real Base64 implementation.
public final class Base64 { public static final int NO_WRAP=2;public static byte[] decode(String value,int flags){return java.util.Base64.getDecoder().decode(value);}public static String encodeToString(byte[] value,int flags){return java.util.Base64.getEncoder().encodeToString(value);} }
