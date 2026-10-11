package com.contentmover.mobile;

import android.content.Context;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.provider.OpenableColumns;
import java.io.*;
import java.util.Locale;

final class ImageAttachments {
    static final int SINGLE_LIMIT=10000000, TOTAL_LIMIT=16000000;
    static String size(long bytes){return bytes<0 ? "大小未知" : String.format(Locale.CHINA,"%.1f MB",bytes/1000000.0);}
    static long sourceSize(Context context,Uri uri){try(Cursor cursor=context.getContentResolver().query(uri,new String[]{OpenableColumns.SIZE},null,null,null)){if(cursor!=null && cursor.moveToFirst() && !cursor.isNull(0))return cursor.getLong(0);}catch(Exception ignored){}return -1;}
    static BitmapFactory.Options bounds(Context context,Uri uri)throws IOException{BitmapFactory.Options options=new BitmapFactory.Options();options.inJustDecodeBounds=true;try(InputStream input=context.getContentResolver().openInputStream(uri)){if(input==null)throw new IOException("无法读取图片");BitmapFactory.decodeStream(input,null,options);}if(options.outWidth<=0 || options.outHeight<=0)throw new IOException("无法识别图片，请换用 PNG 或 JPG 截图");return options;}
    static Bitmap thumbnail(Context context,Uri uri)throws IOException{BitmapFactory.Options info=bounds(context,uri);BitmapFactory.Options options=new BitmapFactory.Options();options.inSampleSize=sample(info.outWidth,info.outHeight,512);try(InputStream input=context.getContentResolver().openInputStream(uri)){Bitmap bitmap=BitmapFactory.decodeStream(input,null,options);if(bitmap==null)throw new IOException("预览读取失败");return bitmap;}}
    static int sample(int width,int height,int bound){int sample=1;while(width/sample>bound || height/sample>bound)sample*=2;return sample;}
    static byte[] prepare(Context context,Uri uri,boolean shrink,int index)throws IOException{
        String prefix="第 "+index+" 张图片：";long size=sourceSize(context,uri);BitmapFactory.Options info;
        try{info=bounds(context,uri);}catch(IOException error){throw new IOException(prefix+error.getMessage());}
        if(!shrink && ((long)info.outWidth*info.outHeight>24000000 || size>SINGLE_LIMIT))throw new IOException(prefix+size(size)+"，"+info.outWidth+" × "+info.outHeight+"。原图上限为 10 MB / 2400 万像素，请勾选“缩小图片再发送”，或移除这张图。");
        boolean nativeFormat="image/png".equals(info.outMimeType) || "image/jpeg".equals(info.outMimeType) || "image/bmp".equals(info.outMimeType);
        if(!shrink && nativeFormat){try(InputStream input=context.getContentResolver().openInputStream(uri)){if(input==null)throw new IOException(prefix+"无法读取");try{return Transfer.read(input,SINGLE_LIMIT);}catch(IOException error){throw new IOException(prefix+"读取失败或超过 10 MB；可勾选“缩小图片再发送”后重试。");}}}
        BitmapFactory.Options options=new BitmapFactory.Options();options.inSampleSize=shrink ? sample(info.outWidth,info.outHeight,5120) : 1;
        Bitmap bitmap;try(InputStream input=context.getContentResolver().openInputStream(uri)){bitmap=BitmapFactory.decodeStream(input,null,options);}if(bitmap==null)throw new IOException(prefix+"无法读取");
        try{if(shrink && Math.max(bitmap.getWidth(),bitmap.getHeight())>2560){double scale=2560.0/Math.max(bitmap.getWidth(),bitmap.getHeight());Bitmap reduced=Bitmap.createScaledBitmap(bitmap,Math.max(1,(int)Math.round(bitmap.getWidth()*scale)),Math.max(1,(int)Math.round(bitmap.getHeight()*scale)),true);if(reduced!=bitmap){bitmap.recycle();bitmap=reduced;}}
            try(ByteArrayOutputStream output=new ByteArrayOutputStream()){if(!bitmap.compress(shrink ? Bitmap.CompressFormat.JPEG : Bitmap.CompressFormat.PNG,90,output))throw new IOException(prefix+"转换失败");if(output.size()>SINGLE_LIMIT)throw new IOException(prefix+"处理后仍超过 10 MB，请换较小图片。");return output.toByteArray();}
        }finally{bitmap.recycle();}
    }
}
