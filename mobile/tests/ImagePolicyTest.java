package com.contentmover.mobile;
import java.io.*;
public class ImagePolicyTest {
  public static void main(String[] args)throws Exception {
    int[][] cases={{1,1},{512,512},{513,800},{8000,6000},{100,20000}};
    for(int[] dims:cases){int sample=ImageAttachments.sample(dims[0],dims[1],512);if(sample<1 || dims[0]/sample>512 || dims[1]/sample>512)throw new AssertionError("Unsafe thumbnail sample");}
    byte[] boundary=new byte[1024];if(Transfer.read(new ByteArrayInputStream(boundary),1024).length!=1024)throw new AssertionError("Boundary rejected");
    try{Transfer.read(new ByteArrayInputStream(new byte[1025]),1024);throw new AssertionError("Oversize accepted");}catch(IOException expected){}
    if(!ImageAttachments.size(12000000).equals("12.0 MB"))throw new AssertionError("Wrong size display");
    System.out.println("PASS: thumbnail sampling, image size display, exact byte limit and overflow rejection");
  }
}
