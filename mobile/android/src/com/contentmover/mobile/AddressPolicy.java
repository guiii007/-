package com.contentmover.mobile;

final class AddressPolicy {
    static int[] octets(String host){
        if(host==null || !host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}"))return null;
        String[] parts=host.split("\\.");int[] result=new int[4];
        for(int i=0;i<4;i++){result[i]=Integer.parseInt(parts[i]);if(result[i]>255)return null;}
        return result;
    }
    static boolean local(String host){int[] a=octets(host);return a!=null && (a[0]==10 || (a[0]==172 && a[1]>=16 && a[1]<=31) || (a[0]==192 && a[1]==168) || (a[0]==169 && a[1]==254));}
    static boolean remote(String host){int[] a=octets(host);return a!=null && a[0]==100 && a[1]>=64 && a[1]<=127 && !(a[1]==100 && a[2]==100 && a[3]==100);}
    static boolean allowed(String host,boolean remote){return remote ? remote(host) : local(host);}
}
